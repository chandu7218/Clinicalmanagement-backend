
using System;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.Tasks;
using AuthDemo.Data;
using AuthDemo.Models;
using AuthDemo.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthDemo.Controllers;

// =====================================================
// PAYMENTS - HOSPITAL BILLING MERCHANT SETTINGS
// Each hospital receives payments in its own account.
// HospitalId is resolved from authenticated Admin.
// =====================================================

public class UpdateHospitalPaymentSettingsDto
{
    [Required]
    [MaxLength(150)]
    public string MerchantName { get; set; } = "";

    [Required]
    [RegularExpression("Test|Live")]
    public string Mode { get; set; } = "Test";

    public bool IsEnabled { get; set; }

    [MaxLength(200)]
    public string? CredentialReference { get; set; }

    // Optional credentials.
    // Leave all three empty to retain existing credentials.
    [MaxLength(200)]
    public string? KeyId { get; set; }

    public string? KeySecret { get; set; }

    public string? WebhookSecret { get; set; }
}

[ApiController]
[Route("api/hospital/payment-settings")]
[Authorize(Roles = "Admin")]
public class HospitalPaymentSettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly RazorpayCredentialStore _credentialStore;

    public HospitalPaymentSettingsController(
        AppDbContext db,
        IConfiguration configuration,
        RazorpayCredentialStore credentialStore)
    {
        _db = db;
        _configuration = configuration;
        _credentialStore = credentialStore;
    }

    // =================================================
    // PAYMENTS - CHECK LEGACY CONFIGURATION
    // =================================================

    private bool HasConfiguredHospitalCredentials(
        int hospitalId,
        string mode)
    {
        var prefix =
            $"Payments:Hospitals:{hospitalId}:Razorpay:{mode}:";

        return !string.IsNullOrWhiteSpace(
                   _configuration[prefix + "KeyId"])
            && !string.IsNullOrWhiteSpace(
                   _configuration[prefix + "KeySecret"])
            && !string.IsNullOrWhiteSpace(
                   _configuration[prefix + "WebhookSecret"]);
    }

    // =================================================
    // PAYMENTS - CHECK SAVED DATABASE CREDENTIALS
    // =================================================

    private async Task<bool> HasSavedHospitalCredentialsAsync(
        int hospitalId,
        string mode)
    {
        return await _db.RazorpayMerchantCredentials
            .AsNoTracking()
            .AnyAsync(x =>
                x.MerchantType == "Hospital" &&
                x.HospitalId == hospitalId &&
                x.Mode == mode &&
                x.IsActive &&
                x.KeyId != "" &&
                x.EncryptedKeySecret != "" &&
                x.EncryptedWebhookSecret != "");
    }

    // =================================================
    // PAYMENTS - CHECK CREDENTIAL AVAILABILITY
    // =================================================

    private async Task<bool> HasHospitalCredentialsAsync(
        int hospitalId,
        string mode)
    {
        if (await HasSavedHospitalCredentialsAsync(
                hospitalId, mode))
        {
            return true;
        }

        return HasConfiguredHospitalCredentials(
            hospitalId, mode);
    }

    // =================================================
    // PAYMENTS - RESOLVE AUTHENTICATED HOSPITAL
    // =================================================

    private async Task<int?> GetHospitalIdAsync()
    {
        var userIdValue =
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!int.TryParse(userIdValue, out var userId))
            return null;

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        if (user == null ||
            !user.IsActive ||
            user.Role != "Admin")
        {
            return null;
        }

        return user.HospitalId;
    }

    // =================================================
    // PAYMENTS - GET OWN HOSPITAL MERCHANT
    // =================================================

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var hospitalId = await GetHospitalIdAsync();

        if (!hospitalId.HasValue ||
            hospitalId.Value <= 0)
        {
            return Forbid();
        }

        var setting = await _db.HospitalMerchantSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.HospitalId == hospitalId.Value);

        if (setting == null)
        {
            return Ok(new
            {
                hospitalId,
                provider = "Razorpay",
                mode = "Test",
                isEnabled = false,
                kycStatus = "NotStarted",
                providerMerchantAccountId = (string?)null,
                credentialConfigured =
                    await HasHospitalCredentialsAsync(
                        hospitalId.Value, "Test")
            });
        }

        return Ok(new
        {
            setting.HospitalId,
            setting.Provider,
            setting.MerchantName,
            setting.Mode,
            setting.IsEnabled,
            setting.KycStatus,
            setting.ProviderMerchantAccountId,
            setting.KycSubmittedAt,
            setting.KycReviewedAt,

            credentialConfigured =
                await HasHospitalCredentialsAsync(
                    setting.HospitalId,
                    setting.Mode),

            setting.UpdatedAt
        });
    }

    // =================================================
    // PAYMENTS - UPDATE OWN HOSPITAL MERCHANT
    // =================================================

    [HttpPut]
    public async Task<IActionResult> Update(
        [FromBody] UpdateHospitalPaymentSettingsDto dto)
    {
        var hospitalId = await GetHospitalIdAsync();

        if (!hospitalId.HasValue ||
            hospitalId.Value <= 0)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(dto.MerchantName))
        {
            return BadRequest(new
            {
                message = "Merchant name is required."
            });
        }

        if (dto.Mode is not ("Test" or "Live"))
        {
            return BadRequest(new
            {
                message = "Mode must be Test or Live."
            });
        }

        // =============================================
        // VALIDATE CREDENTIAL INPUT
        // =============================================

        bool hasKeyId =
            !string.IsNullOrWhiteSpace(dto.KeyId);

        bool hasKeySecret =
            !string.IsNullOrWhiteSpace(dto.KeySecret);

        bool hasWebhookSecret =
            !string.IsNullOrWhiteSpace(dto.WebhookSecret);

        bool hasAnyCredential =
            hasKeyId ||
            hasKeySecret ||
            hasWebhookSecret;

        bool hasAllCredentials =
            hasKeyId &&
            hasKeySecret &&
            hasWebhookSecret;

        if (hasAnyCredential && !hasAllCredentials)
        {
            return BadRequest(new
            {
                message =
                    "Provide KeyId, KeySecret and " +
                    "WebhookSecret together."
            });
        }

        if (hasAllCredentials)
        {
            if (dto.KeyId!.Trim().Length > 200)
            {
                return BadRequest(new
                {
                    message = "KeyId is too long."
                });
            }

            if (dto.KeySecret!.Length > 4096 ||
                dto.WebhookSecret!.Length > 4096)
            {
                return BadRequest(new
                {
                    message = "Credential length is invalid."
                });
            }

            if (dto.Mode == "Test" &&
                !dto.KeyId.Trim().StartsWith(
                    "rzp_test_",
                    StringComparison.Ordinal))
            {
                return BadRequest(new
                {
                    message =
                        "Test mode requires a Razorpay Test KeyId."
                });
            }

            if (dto.Mode == "Live" &&
                !dto.KeyId.Trim().StartsWith(
                    "rzp_live_",
                    StringComparison.Ordinal))
            {
                return BadRequest(new
                {
                    message =
                        "Live mode requires a Razorpay Live KeyId."
                });
            }
        }

        // =============================================
        // LOAD EXISTING MERCHANT SETTINGS
        // =============================================

        var setting = await _db.HospitalMerchantSettings
            .FirstOrDefaultAsync(
                x => x.HospitalId == hospitalId.Value);

        // Preserve existing KYC approval requirements.
        if (dto.IsEnabled &&
            (setting == null ||
             setting.KycStatus != "Approved" ||
             string.IsNullOrWhiteSpace(
                 setting.ProviderMerchantAccountId)))
        {
            return Conflict(new
            {
                message =
                    "Hospital Razorpay merchant must " +
                    "complete provider KYC and be " +
                    "approved by Super Admin " +
                    "before activation."
            });
        }

        // Allow activation with either new credentials supplied in this
        // request, previously saved active database credentials, or legacy
        // server configuration credentials for the selected mode.
        // KYC approval is still required above.
        if (dto.IsEnabled &&
            !hasAllCredentials &&
            !await HasHospitalCredentialsAsync(hospitalId.Value, dto.Mode))
        {
            return Conflict(new
            {
                message =
                    "Configure Razorpay KeyId, KeySecret and WebhookSecret " +
                    "for the selected mode before enabling payments."
            });
        }

        // Prevent rotating credentials while the merchant
        // is enabled. Existing payment verification and
        // refunds must be migrated first.
        if (hasAllCredentials &&
            setting?.IsEnabled == true)
        {
            return Conflict(new
            {
                message =
                    "Disable this merchant before updating " +
                    "its credentials. Complete payment " +
                    "credential-version integration before " +
                    "rotating credentials used by payments."
            });
        }

        await using var transaction =
            await _db.Database.BeginTransactionAsync();

        // =============================================
        // SAVE ENCRYPTED RAZORPAY CREDENTIALS
        // =============================================

        if (hasAllCredentials)
        {
            var existingCredentials =
                await _db.RazorpayMerchantCredentials
                    .Where(x =>
                        x.MerchantType == "Hospital" &&
                        x.HospitalId == hospitalId.Value &&
                        x.Mode == dto.Mode &&
                        x.IsActive)
                    .ToListAsync();

            var now = DateTime.UtcNow;

            foreach (var credential in existingCredentials)
            {
                credential.IsActive = false;
                credential.RetiredAt = now;
            }

            // Save deactivations first to respect the
            // filtered unique index in SQL Server.
            if (existingCredentials.Count > 0)
            {
                await _db.SaveChangesAsync();
            }

            var newCredential =
                new RazorpayMerchantCredential
                {
                    MerchantType = "Hospital",
                    HospitalId = hospitalId.Value,
                    Mode = dto.Mode,
                    KeyId = dto.KeyId!.Trim(),

                    EncryptedKeySecret =
                        _credentialStore.Encrypt(
                            dto.KeySecret!),

                    EncryptedWebhookSecret =
                        _credentialStore.Encrypt(
                            dto.WebhookSecret!),

                    IsActive = true,
                    CreatedAt = now
                };

            _db.RazorpayMerchantCredentials.Add(
                newCredential);
        }

        // =============================================
        // CREATE MERCHANT SETTING IF MISSING
        // =============================================

        if (setting == null)
        {
            setting = new HospitalMerchantSetting
            {
                HospitalId = hospitalId.Value,
                Provider = "Razorpay"
            };

            _db.HospitalMerchantSettings.Add(setting);
        }

        // =============================================
        // UPDATE MERCHANT SETTINGS
        // =============================================

        setting.MerchantName = dto.MerchantName.Trim();
        setting.Mode = dto.Mode;
        setting.IsEnabled = dto.IsEnabled;

        // Preserve an existing credential reference
        // when the request does not supply a new one.
        if (dto.CredentialReference != null)
        {
            setting.CredentialReference =
                string.IsNullOrWhiteSpace(
                    dto.CredentialReference)
                    ? null
                    : dto.CredentialReference.Trim();
        }

        setting.UpdatedAt = DateTime.UtcNow;

        // =============================================
        // AUDIT LOG
        // Never log private credentials.
        // =============================================

        if (int.TryParse(
            User.FindFirstValue(
                ClaimTypes.NameIdentifier),
            out var actorId))
        {
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,

                UserName =
                    User.FindFirstValue(ClaimTypes.Name)
                    ?? User.Identity?.Name
                    ?? "Admin",

                Role = "Admin",

                ClinicId = hospitalId.Value,

                Action =
                    "Hospital Payment Merchant Settings Updated",

                SystemAction =
                    $"Hospital merchant mode={setting.Mode}, " +
                    $"enabled={setting.IsEnabled}, " +
                    $"KYC={setting.KycStatus}, " +
                    $"credentialsUpdated={hasAllCredentials}.",

                IsLoginActivity = false,

                IpAddress =
                    HttpContext.Connection.RemoteIpAddress?
                        .ToString(),

                Browser =
                    Request.Headers.UserAgent.ToString(),

                Timestamp = DateTime.UtcNow,
                LoginTime = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new
        {
            message =
                "Hospital merchant settings saved successfully.",

            setting.HospitalId,
            setting.Provider,
            setting.MerchantName,
            setting.Mode,
            setting.IsEnabled,
            setting.KycStatus,
            setting.ProviderMerchantAccountId,
            setting.KycSubmittedAt,
            setting.KycReviewedAt,

            credentialConfigured =
                await HasHospitalCredentialsAsync(
                    setting.HospitalId,
                    setting.Mode),

            setting.UpdatedAt
        });
    }
}
