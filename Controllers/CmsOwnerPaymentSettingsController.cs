using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AuthDemo.Data;
using AuthDemo.Models;
using AuthDemo.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthDemo.Controllers;

public class UpdateCmsOwnerPaymentSettingsDto
{
    [Required, MaxLength(150)]
    public string MerchantName { get; set; } = "";

    [Required, RegularExpression("^(Test|Live)$")]
    public string Mode { get; set; } = "Test";

    public bool IsEnabled { get; set; }

    [MaxLength(200)]
    public string? CredentialReference { get; set; }

    // Optional: omit all three to retain existing credentials.
    [MaxLength(200)]
    public string? KeyId { get; set; }
    public string? KeySecret { get; set; }
    public string? WebhookSecret { get; set; }
}

[ApiController]
[Route("api/cms-owner/payment-settings")]
[Authorize(Roles = "SuperAdmin")]
public class CmsOwnerPaymentSettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly RazorpayCredentialStore _credentialStore;

    public CmsOwnerPaymentSettingsController(
        AppDbContext db,
        IConfiguration configuration,
        RazorpayCredentialStore credentialStore)
    {
        _db = db;
        _configuration = configuration;
        _credentialStore = credentialStore;
    }

    private bool HasLegacyCmsCredentials(string mode)
    {
        var prefix = $"Payments:CmsOwner:Razorpay:{mode}:";
        return !string.IsNullOrWhiteSpace(_configuration[prefix + "KeyId"])
            && !string.IsNullOrWhiteSpace(_configuration[prefix + "KeySecret"])
            && !string.IsNullOrWhiteSpace(_configuration[prefix + "WebhookSecret"]);
    }

    private async Task<bool> HasCmsCredentialsAsync(string mode)
    {
        var hasDatabaseCredentials = await _db.RazorpayMerchantCredentials
            .AsNoTracking()
            .AnyAsync(x => x.MerchantType == "CMS"
                && x.HospitalId == null
                && x.Mode == mode
                && x.IsActive);

        return hasDatabaseCredentials || HasLegacyCmsCredentials(mode);
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var setting = await _db.CmsOwnerMerchantSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == 1);

        if (setting == null)
        {
            return Ok(new
            {
                provider = "Razorpay",
                merchantName = "CMS Owner",
                mode = "Test",
                isEnabled = false,
                kycStatus = "NotStarted",
                providerMerchantAccountId = (string?)null,
                credentialConfigured = await HasCmsCredentialsAsync("Test")
            });
        }

        return Ok(new
        {
            setting.Provider,
            setting.MerchantName,
            setting.Mode,
            setting.IsEnabled,
            setting.KycStatus,
            setting.ProviderMerchantAccountId,
            setting.KycSubmittedAt,
            setting.KycReviewedAt,
            credentialConfigured = await HasCmsCredentialsAsync(setting.Mode),
            setting.UpdatedAt
        });
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateCmsOwnerPaymentSettingsDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.MerchantName))
            return BadRequest(new { message = "Merchant name is required." });
        if (dto.Mode is not ("Test" or "Live"))
            return BadRequest(new { message = "Mode must be Test or Live." });

        bool hasKeyId = !string.IsNullOrWhiteSpace(dto.KeyId);
        bool hasKeySecret = !string.IsNullOrWhiteSpace(dto.KeySecret);
        bool hasWebhookSecret = !string.IsNullOrWhiteSpace(dto.WebhookSecret);
        bool supplyingCredentials = hasKeyId || hasKeySecret || hasWebhookSecret;

        if (supplyingCredentials && !(hasKeyId && hasKeySecret && hasWebhookSecret))
            return BadRequest(new { message = "Supply KeyId, KeySecret and WebhookSecret together, or omit all three to keep existing credentials." });

        if (supplyingCredentials)
        {
            var keyId = dto.KeyId!.Trim();
            var expectedPrefix = dto.Mode == "Test" ? "rzp_test_" : "rzp_live_";
            if (!keyId.StartsWith(expectedPrefix, StringComparison.Ordinal))
                return BadRequest(new { message = "Razorpay KeyId does not match the selected Test/Live mode." });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();

        var setting = await _db.CmsOwnerMerchantSettings
            .FirstOrDefaultAsync(x => x.Id == 1);

        if (dto.IsEnabled && (setting == null || setting.KycStatus != "Approved" ||
            string.IsNullOrWhiteSpace(setting.ProviderMerchantAccountId)))
        {
            return Conflict(new { message = "CMS Owner Razorpay merchant must complete provider KYC and be approved before activation." });
        }

        if (dto.IsEnabled && !supplyingCredentials && !await HasCmsCredentialsAsync(dto.Mode))
        {
            return Conflict(new { message = "Configure CMS Owner Razorpay credentials before enabling payments." });
        }

        if (supplyingCredentials)
        {
            // Retire the old version first to satisfy the filtered unique index.
            var activeCredentials = await _db.RazorpayMerchantCredentials
                .Where(x => x.MerchantType == "CMS" && x.HospitalId == null &&
                            x.Mode == dto.Mode && x.IsActive)
                .ToListAsync();

            var now = DateTime.UtcNow;
            foreach (var credential in activeCredentials)
            {
                credential.IsActive = false;
                credential.RetiredAt = now;
            }
            if (activeCredentials.Count > 0)
                await _db.SaveChangesAsync();

            _db.RazorpayMerchantCredentials.Add(new RazorpayMerchantCredential
            {
                MerchantType = "CMS",
                HospitalId = null,
                Mode = dto.Mode,
                KeyId = dto.KeyId!.Trim(),
                EncryptedKeySecret = _credentialStore.Encrypt(dto.KeySecret!),
                EncryptedWebhookSecret = _credentialStore.Encrypt(dto.WebhookSecret!),
                IsActive = true,
                CreatedAt = now
            });
        }

        if (setting == null)
        {
            setting = new CmsOwnerMerchantSetting
            {
                Id = 1,
                Provider = "Razorpay"
            };
            _db.CmsOwnerMerchantSettings.Add(setting);
        }

        setting.MerchantName = dto.MerchantName.Trim();
        setting.Mode = dto.Mode;
        setting.IsEnabled = dto.IsEnabled;
        // Omission retains the existing reference for backward compatibility.
        if (dto.CredentialReference != null)
            setting.CredentialReference = string.IsNullOrWhiteSpace(dto.CredentialReference)
                ? null : dto.CredentialReference.Trim();
        setting.UpdatedAt = DateTime.UtcNow;

        if (int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
        {
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,
                UserName = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "SuperAdmin",
                Role = "SuperAdmin",
                Action = "CMS Owner Payment Merchant Settings Updated",
                SystemAction = $"CMS Owner merchant mode={setting.Mode}, enabled={setting.IsEnabled}, KYC={setting.KycStatus}, credentialsUpdated={supplyingCredentials}.",
                IsLoginActivity = false,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Browser = Request.Headers.UserAgent.ToString(),
                Timestamp = DateTime.UtcNow,
                LoginTime = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new
        {
            message = "CMS owner merchant settings saved.",
            setting.Provider,
            setting.MerchantName,
            setting.Mode,
            setting.IsEnabled,
            setting.KycStatus,
            setting.ProviderMerchantAccountId,
            setting.KycSubmittedAt,
            setting.KycReviewedAt,
            credentialConfigured = await HasCmsCredentialsAsync(setting.Mode)
        });
    }
}
