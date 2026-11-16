using System.ComponentModel.DataAnnotations;

namespace AuthDemo.Models;

public sealed class HospitalSettlementRecord
{
    public long Id { get; set; }
    public int HospitalId { get; set; }
    public long HospitalPaymentTransactionId { get; set; }
    public HospitalPaymentTransaction Transaction { get; set; } = null!;
    [Required, MaxLength(150)] public string SettlementId { get; set; } = "";
    public decimal GrossAmount { get; set; }
    public decimal FeeAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal NetAmount { get; set; }
    public DateTime SettledAt { get; set; }
    [Required, MaxLength(30)] public string ReconciliationStatus { get; set; } = "Matched";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
