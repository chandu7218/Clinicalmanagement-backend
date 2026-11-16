
using System;
using System.ComponentModel.DataAnnotations;

namespace AuthDemo.Models;

public class RazorpayMerchantCredential
{
    public int Id { get; set; }

    // CMS or Hospital
    [Required, MaxLength(20)]
    public string MerchantType { get; set; } = "";

    // Null for CMS; populated for Hospital
    public int? HospitalId { get; set; }

    [Required, MaxLength(20)]
    public string Mode { get; set; } = "Test";

    // Public Razorpay identifier
    [Required, MaxLength(200)]
    public string KeyId { get; set; } = "";

    // Encrypted using ASP.NET Core Data Protection
    [Required]
    public string EncryptedKeySecret { get; set; } = "";

    [Required]
    public string EncryptedWebhookSecret { get; set; } = "";

    // One active credential per merchant and mode
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? RetiredAt { get; set; }
}
