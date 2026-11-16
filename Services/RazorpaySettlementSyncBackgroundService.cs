using AuthDemo.Data;
using AuthDemo.Models;
using Microsoft.EntityFrameworkCore;

namespace AuthDemo.Services;

/// <summary>
/// Optional scheduled payout-level settlement synchronization. Disabled by default;
/// enable with Payments:SettlementSync:Enabled=true after merchant credentials are configured.
/// </summary>
public sealed class RazorpaySettlementSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RazorpaySettlementSyncBackgroundService> _logger;

    public RazorpaySettlementSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<RazorpaySettlementSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue<bool>("Payments:SettlementSync:Enabled");
        var intervalHours = Math.Clamp(
            _configuration.GetValue<int?>("Payments:SettlementSync:IntervalHours") ?? 24, 1, 168);
        var lookbackDays = Math.Clamp(
            _configuration.GetValue<int?>("Payments:SettlementSync:LookbackDays") ?? 7, 1, 93);

        if (!enabled)
        {
            _logger.LogInformation("Razorpay settlement background sync is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAllAsync(lookbackDays, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled Razorpay settlement sync failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SyncAllAsync(int lookbackDays, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var to = DateTime.UtcNow;
        var from = to.AddDays(-lookbackDays);

        // Keep hospital and CMS-owner settlement data strictly separate.
        var hospitals = await db.HospitalMerchantSettings
            .AsNoTracking()
            .Where(x => x.KycStatus == "Approved" &&
                        x.ProviderMerchantAccountId != null &&
                        x.ProviderMerchantAccountId != "")
            .ToListAsync(cancellationToken);

        foreach (var merchant in hospitals)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A separate DbContext scope per merchant prevents tracked changes
            // from a failed save being accidentally written for another merchant.
            await using var merchantScope = _scopeFactory.CreateAsyncScope();
            var merchantDb = merchantScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var merchantGateway = merchantScope.ServiceProvider.GetRequiredService<HospitalRazorpayGatewayService>();

            try
            {
                var settlements = await merchantGateway.FetchSettlementsAsync(
                    merchant.HospitalId, merchant.Mode, from, to);

                // Deduplicate the provider response before inserting new rows.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in settlements)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(item.Id))
                    {
                        _logger.LogWarning(
                            "Skipping hospital settlement with missing ID for hospital {HospitalId} and mode {Mode}.",
                            merchant.HospitalId, merchant.Mode);
                        continue;
                    }

                    if (!seen.Add(item.Id))
                        continue;

                    var row = await merchantDb.HospitalGatewaySettlements
                        .FirstOrDefaultAsync(x =>
                            x.HospitalId == merchant.HospitalId &&
                            x.Mode == merchant.Mode &&
                            x.SettlementId == item.Id,
                            cancellationToken);

                    if (row == null)
                    {
                        row = new HospitalGatewaySettlement
                        {
                            HospitalId = merchant.HospitalId,
                            Mode = merchant.Mode,
                            SettlementId = item.Id
                        };
                        merchantDb.HospitalGatewaySettlements.Add(row);
                    }

                    row.Status = item.Status;
                    row.Amount = item.Amount;
                    row.FeeAmount = item.Fees;
                    row.TaxAmount = item.Tax;
                    row.Utr = item.Utr;
                    row.SettledAt = item.CreatedAt;
                    row.SyncedAt = DateTime.UtcNow;
                }

                await merchantDb.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Settlement sync failed for hospital {HospitalId}, mode {Mode}.",
                    merchant.HospitalId, merchant.Mode);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var cmsMerchant = await db.CmsOwnerMerchantSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);

        if (cmsMerchant == null ||
            cmsMerchant.KycStatus != "Approved" ||
            string.IsNullOrWhiteSpace(cmsMerchant.ProviderMerchantAccountId))
        {
            return;
        }

        await using var cmsScope = _scopeFactory.CreateAsyncScope();
        var cmsDb = cmsScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cmsScopedGateway = cmsScope.ServiceProvider.GetRequiredService<CmsSubscriptionRazorpayService>();

        try
        {
            var settlements = await cmsScopedGateway.FetchSettlementsAsync(
                cmsMerchant.Mode, from, to);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in settlements)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    _logger.LogWarning(
                        "Skipping CMS-owner settlement with missing ID in mode {Mode}.",
                        cmsMerchant.Mode);
                    continue;
                }

                if (!seen.Add(item.Id))
                    continue;

                var row = await cmsDb.CmsGatewaySettlements
                    .FirstOrDefaultAsync(x =>
                        x.Mode == cmsMerchant.Mode &&
                        x.SettlementId == item.Id,
                        cancellationToken);

                if (row == null)
                {
                    row = new CmsGatewaySettlement
                    {
                        Mode = cmsMerchant.Mode,
                        SettlementId = item.Id
                    };
                    cmsDb.CmsGatewaySettlements.Add(row);
                }

                row.Status = item.Status;
                row.Amount = item.Amount;
                row.FeeAmount = item.Fees;
                row.TaxAmount = item.Tax;
                row.Utr = item.Utr;
                row.SettledAt = item.CreatedAt;
                row.SyncedAt = DateTime.UtcNow;
            }

            await cmsDb.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settlement sync failed for CMS Owner, mode {Mode}.", cmsMerchant.Mode);
        }
    }
}
