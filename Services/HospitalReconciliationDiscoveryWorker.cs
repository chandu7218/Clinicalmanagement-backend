using System.Data;
using AuthDemo.Data;
using AuthDemo.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AuthDemo.Services;

// Disabled by default. Discovers unresolved local payment references; never moves money.
public sealed class HospitalReconciliationDiscoveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<HospitalReconciliationDiscoveryWorker> _logger;

    public HospitalReconciliationDiscoveryWorker(IServiceScopeFactory scopes, IConfiguration config,
        ILogger<HospitalReconciliationDiscoveryWorker> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue<bool>("HospitalReconciliationWorker:Enabled"))
        {
            _logger.LogInformation("Hospital reconciliation discovery worker disabled");
            return;
        }
        var interval = Math.Clamp(_config.GetValue("HospitalReconciliationWorker:IntervalMinutes", 30), 5, 1440);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(interval));
        do
        {
            try { await DiscoverAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Reconciliation discovery iteration failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DiscoverAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Session-scoped SQL application lock, shared across all app instances using this DB.
        // Hold the same connection for the entire scan; SQL releases lock on connection close.
        await db.Database.OpenConnectionAsync(ct);
        var connection = db.Database.GetDbConnection();
        try
        {
            await using var lockCommand = connection.CreateCommand();
            lockCommand.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource = N'Adminclinic:HospitalReconciliationDiscovery:v1', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @result;";
            var lockResult = Convert.ToInt32(await lockCommand.ExecuteScalarAsync(ct));
            if (lockResult < 0) { _logger.LogInformation("Discovery skipped: another worker holds the lease"); return; }
            try
            {
                var age = Math.Clamp(_config.GetValue("HospitalReconciliationWorker:MinimumAgeMinutes", 15), 1, 1440);
                var cutoff = DateTime.UtcNow.AddMinutes(-age);
                var orders = await db.HospitalPaymentOrders.AsNoTracking()
                    .Where(x => x.CreatedAt <= cutoff && x.GatewayPaymentId != null && x.GatewayPaymentId != ""
                        && x.GatewayOrderId != null && x.GatewayOrderId != ""
                        && !db.HospitalPaymentTransactions.Any(t => t.HospitalId == x.HospitalId
                            && t.GatewayPaymentId == x.GatewayPaymentId && t.Status == "Captured"))
                    .GroupBy(x => x.HospitalId)
                    .Select(g => new { HospitalId = g.Key, Count = g.Count() })
                    .ToListAsync(ct);
                var qrs = await db.HospitalPaymentQrs.AsNoTracking()
                    .Where(x => x.CreatedAtUtc <= cutoff && x.GatewayPaymentId != null && x.GatewayPaymentId != ""
                        && x.GatewayQrId != null && x.GatewayQrId != ""
                        && !db.HospitalPaymentTransactions.Any(t => t.HospitalId == x.HospitalId
                            && t.GatewayPaymentId == x.GatewayPaymentId && t.Status == "Captured"))
                    .GroupBy(x => x.HospitalId)
                    .Select(g => new { HospitalId = g.Key, Count = g.Count() })
                    .ToListAsync(ct);
                var hospitalIds = orders.Select(x => x.HospitalId).Union(qrs.Select(x => x.HospitalId)).ToArray();
                var now = DateTime.UtcNow;
                foreach (var id in hospitalIds)
                {
                    var orderCount = orders.FirstOrDefault(x => x.HospitalId == id)?.Count ?? 0;
                    var qrCount = qrs.FirstOrDefault(x => x.HospitalId == id)?.Count ?? 0;
                    db.AuditLogs.Add(new AuditLog
                    {
                        UserId = 0,
                        UserName = "System",
                        Role = "System",
                        ClinicId = id,
                        Action = "HospitalReconciliationDiscovery",
                        Timestamp = now,
                        LoginTime = now,
                        SystemAction = $"ReadOnlyDiscovery; orderCandidates={orderCount}; qrCandidates={qrCount}; minimumAgeMinutes={age}; noGatewayCalls=true; noAllocations=true"
                    });
                }
                if (hospitalIds.Length > 0) await db.SaveChangesAsync(ct);
                _logger.LogInformation("Reconciliation discovery completed for {HospitalCount} hospitals", hospitalIds.Length);
            }
            finally
            {
                await using var release = connection.CreateCommand();
                release.CommandText = "EXEC sp_releaseapplock @Resource = N'Adminclinic:HospitalReconciliationDiscovery:v1', @LockOwner = N'Session';";
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
