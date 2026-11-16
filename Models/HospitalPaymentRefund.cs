using System.ComponentModel.DataAnnotations;

namespace AuthDemo.Models;

public sealed class HospitalPaymentRefund
{
    public long Id { get; set; }
    public long HospitalPaymentTransactionId { get; set; }
    public HospitalPaymentTransaction Transaction { get; set; } = null!;
    public int HospitalId { get; set; }
    public int BillingId { get; set; }
    [Required, MaxLength(150)] public string GatewayRefundId { get; set; } = "";
    public decimal Amount { get; set; }
    [Required, MaxLength(30)] public string Status { get; set; } = "Requested";
    [MaxLength(300)] public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
