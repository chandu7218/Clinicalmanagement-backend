
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AuthDemo.Data;
using AuthDemo.Models;
using Microsoft.EntityFrameworkCore;

namespace AuthDemo.Services;

/// <summary>
/// Tracks payment recovery attempts in the database.
/// Does not contact Razorpay or allocate payments.
/// </summary>
public sealed class HospitalPaymentRecoveryService
{
    private readonly AppDbContext _db;
    private const int MaxAttempts = 5;

    public HospitalPaymentRecoveryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<HospitalPaymentRecoveryAttempt> StartAttemptAsync(
        int hospitalId,
        string paymentSource,
        string gatewayPaymentId,
        long? orderId = null,
        long? qrId = null,
        CancellationToken cancellationToken = default)
    {
        if (hospitalId <= 0)
            throw new ArgumentOutOfRangeException(nameof(hospitalId));

        if (paymentSource != "Order" && paymentSource != "Qr")
            throw new ArgumentException(
                "Payment source must be Order or Qr.",
                nameof(paymentSource));

        if (string.IsNullOrWhiteSpace(gatewayPaymentId))
            throw new ArgumentException(
                "Gateway payment ID is required.",
                nameof(gatewayPaymentId));

        if ((paymentSource == "Order" &&
             (!orderId.HasValue || qrId.HasValue)) ||
            (paymentSource == "Qr" &&
             (!qrId.HasValue || orderId.HasValue)))
        {
            throw new ArgumentException(
                "Exactly one matching local payment source ID is required.");
        }

        // This check is not concurrency-safe.
        // Database locking will be added in the next step.
        var history = await _db.HospitalPaymentRecoveryAttempts
            .AsNoTracking()
            .Where(x =>
                x.HospitalId == hospitalId &&
                x.GatewayPaymentId == gatewayPaymentId)
            .OrderByDescending(x => x.AttemptNumber)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (history?.Status is
            "Processing" or "Succeeded" or "ManualReview")
        {
            throw new InvalidOperationException(
                "Payment is already processing, recovered, " +
                "or requires manual review.");
        }

        if (history != null &&
            history.AttemptNumber >= MaxAttempts)
        {
            throw new InvalidOperationException(
                "Maximum recovery attempts reached; manual review required.");
        }

        if (history?.NextRetryAtUtc is DateTime retryAt &&
            retryAt > DateTime.UtcNow)
        {
            throw new InvalidOperationException(
                "The next retry time has not been reached.");
        }

        var attempt = new HospitalPaymentRecoveryAttempt
        {
            HospitalId = hospitalId,
            PaymentSource = paymentSource,
            GatewayPaymentId = gatewayPaymentId.Trim(),
            HospitalPaymentOrderId = orderId,
            HospitalPaymentQrId = qrId,
            AttemptNumber = (history?.AttemptNumber ?? 0) + 1,
            Status = "Processing",
            StartedAtUtc = DateTime.UtcNow
        };

        _db.HospitalPaymentRecoveryAttempts.Add(attempt);

        await _db.SaveChangesAsync(cancellationToken);

        return attempt;
    }

    public async Task CompleteAttemptAsync(
        long attemptId,
        string status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (status != "Succeeded" &&
            status != "Failed" &&
            status != "ManualReview")
        {
            throw new ArgumentException(
                "Status must be Succeeded, Failed, or ManualReview.",
                nameof(status));
        }

        var attempt = await _db.HospitalPaymentRecoveryAttempts
            .SingleOrDefaultAsync(
                x => x.Id == attemptId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Recovery attempt was not found.");

        if (attempt.Status != "Processing")
        {
            throw new InvalidOperationException(
                "Only a processing attempt can be completed.");
        }

        attempt.Status = status;

        attempt.ErrorMessage =
            string.IsNullOrWhiteSpace(errorMessage)
                ? null
                : errorMessage.Length <= 2000
                    ? errorMessage
                    : errorMessage.Substring(0, 2000);

        attempt.CompletedAtUtc = DateTime.UtcNow;

        attempt.NextRetryAtUtc =
            status == "Failed" &&
            attempt.AttemptNumber < MaxAttempts
                ? DateTime.UtcNow.AddMinutes(
                    Math.Min(
                        60,
                        5 * (1 << (attempt.AttemptNumber - 1))))
                : null;

        await _db.SaveChangesAsync(cancellationToken);
    }
}
