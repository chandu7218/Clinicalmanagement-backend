using System.Data;
using AuthDemo.Data;
using AuthDemo.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthDemo.Services;

/// <summary>
/// Shared order creation for an already-authorized hospital/billing context.
/// The caller MUST authorize access to the bill before calling this service.
/// </summary>
public sealed class HospitalPaymentOrderService
{
    private readonly AppDbContext _db;
    private readonly HospitalRazorpayGatewayService _gateway;
    private readonly ILogger<HospitalPaymentOrderService> _logger;

    public HospitalPaymentOrderService(
        AppDbContext db,
        HospitalRazorpayGatewayService gateway,
        ILogger<HospitalPaymentOrderService> logger)
    {
        _db = db;
        _gateway = gateway;
        _logger = logger;
    }

    public async Task<IActionResult> CreateAsync(
        int hospitalId,
        Billing bill,
        decimal? amount,
        string? idempotencyKey)
    {
        if (hospitalId <= 0 || bill.HospitalId != hospitalId)
            return new ForbidResult();

        if (bill.TotalAmount <= 0)
            return new BadRequestObjectResult(new { message = "Invoice total must be greater than zero." });

        idempotencyKey = idempotencyKey?.Trim();
        if (string.IsNullOrEmpty(idempotencyKey))
            idempotencyKey = null;

        if (idempotencyKey?.Length > 100)
            return new BadRequestObjectResult(new { message = "IdempotencyKey cannot exceed 100 characters." });

        // Reuse only an order for the SAME hospital, invoice and requested amount.
        if (idempotencyKey != null)
        {
            var previous = await _db.HospitalPaymentOrders.AsNoTracking()
                .FirstOrDefaultAsync(x => x.HospitalId == hospitalId && x.IdempotencyKey == idempotencyKey);

            if (previous != null)
            {
                if (previous.BillingId != bill.Id || (amount.HasValue && previous.Amount != amount.Value))
                    return new ConflictObjectResult(new { message = "This idempotency key belongs to a different invoice or amount." });

                if (previous.Status != "Pending" || string.IsNullOrWhiteSpace(previous.GatewayOrderId)
                    || (previous.ExpiresAt.HasValue && previous.ExpiresAt <= DateTime.UtcNow))
                    return new ConflictObjectResult(new { message = "This payment attempt is no longer pending. Start a new attempt." });

                try
                {
                    var previousKey = _gateway.PublicKey(hospitalId, previous.Mode, previous.RazorpayCredentialId);
                    return new OkObjectResult(new
                    {
                        previous.LocalOrderId,
                        gatewayOrderId = previous.GatewayOrderId,
                        amountPaise = checked((long)(previous.Amount * 100m)),
                        currency = previous.Currency,
                        publicKey = previousKey,
                        status = previous.Status,
                        reused = true
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to retrieve pinned payment credentials for order {OrderId}", previous.Id);
                    return new ObjectResult(new { message = "Payment credentials are unavailable." }) { StatusCode = 502 };
                }
            }
        }

        var merchant = await _db.HospitalMerchantSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.HospitalId == hospitalId);

        if (merchant == null || !merchant.IsEnabled || merchant.KycStatus != "Approved"
            || string.IsNullOrWhiteSpace(merchant.ProviderMerchantAccountId))
            return new ConflictObjectResult(new { message = "Hospital Razorpay merchant is not approved or enabled." });

        if (merchant.Mode is not ("Test" or "Live"))
            return new ConflictObjectResult(new { message = "Invalid hospital Razorpay mode." });

        // Retain the serializable reservation approach from the existing controller.
        await using var reservation = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var paid = await _db.BillingPaymentTransactions.AsNoTracking()
            .Where(x => x.HospitalId == hospitalId && x.BillingId == bill.Id && x.Status == "Paid")
            .SumAsync(x => (decimal?)x.Amount) ?? 0m;

        var refunded = await _db.HospitalPaymentRefunds.AsNoTracking()
            .Where(x => x.HospitalId == hospitalId && x.BillingId == bill.Id && x.Status == "processed")
            .SumAsync(x => (decimal?)x.Amount) ?? 0m;

        var alreadyPaid = Math.Max(0m, paid - refunded);

        var reserved = await _db.HospitalPaymentOrders
            .Where(x => x.HospitalId == hospitalId && x.BillingId == bill.Id
                && (x.Status == "Pending" || x.Status == "Creating")
                && (!x.ExpiresAt.HasValue || x.ExpiresAt > DateTime.UtcNow))
            .SumAsync(x => (decimal?)x.Amount) ?? 0m;

        var available = Math.Round(bill.TotalAmount - alreadyPaid - reserved, 2,
            MidpointRounding.AwayFromZero);

        if (available <= 0)
            return new ConflictObjectResult(new
            {
                message = "Invoice has no unreserved outstanding balance.",
                outstandingAmount = Math.Max(0m, bill.TotalAmount - alreadyPaid),
                reservedAmount = reserved
            });

        var charge = amount ?? available;
        if (charge <= 0 || charge > available || decimal.Round(charge, 2) != charge)
            return new BadRequestObjectResult(new
            {
                message = "Payment amount must be positive and cannot exceed the available balance.",
                availableAmount = available
            });

        var order = new HospitalPaymentOrder
        {
            HospitalId = hospitalId,
            BillingId = bill.Id,
            PatientId = bill.PatientId,
            BranchId = bill.BranchId,
            Amount = charge,
            Mode = merchant.Mode,
            IdempotencyKey = idempotencyKey,
            Status = "Creating",
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        _db.HospitalPaymentOrders.Add(order);
        await _db.SaveChangesAsync();
        await reservation.CommitAsync();

        try
        {
            var created = await _gateway.CreateOrderWithCredentialAsync(
                hospitalId, merchant.Mode, charge, order.LocalOrderId[..32]);

            order.GatewayOrderId = created.GatewayOrderId;
            order.RazorpayCredentialId = created.RazorpayCredentialId;
            order.Status = "Pending";
            order.ExpiresAt = DateTime.UtcNow.AddHours(24);
            await _db.SaveChangesAsync();

            return new OkObjectResult(new
            {
                order.LocalOrderId,
                gatewayOrderId = order.GatewayOrderId,
                amountPaise = checked((long)(charge * 100m)),
                currency = "INR",
                publicKey = _gateway.PublicKey(hospitalId, merchant.Mode, order.RazorpayCredentialId),
                status = order.Status,
                outstandingAmount = available,
                mode = merchant.Mode
            });
        }
        catch (Exception ex)
        {
            // A gateway request may succeed remotely even if the local response fails.
            // Do not automatically retry with the same receipt without reconciliation.
            order.Status = "Failed";
            try { await _db.SaveChangesAsync(); }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Unable to mark order {OrderId} failed", order.Id);
            }

            _logger.LogError(ex, "Hospital Razorpay order creation failed: hospital {HospitalId}, invoice {BillingId}, local order {LocalOrderId}",
                hospitalId, bill.Id, order.LocalOrderId);

            return new ObjectResult(new { message = "Unable to create payment order. Please check payment status before retrying." })
            { StatusCode = 502 };
        }
    }
}
