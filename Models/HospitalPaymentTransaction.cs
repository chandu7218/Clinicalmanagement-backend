using System.ComponentModel.DataAnnotations;

namespace AuthDemo.Models;

public sealed class HospitalPaymentTransaction
{
    public long Id { get; set; }
    // Standard checkout payments reference an order; QR payments reference a QR.
    public long? HospitalPaymentOrderId { get; set; }
    public long? HospitalPaymentQrId { get; set; }
    public HospitalPaymentOrder? Order { get; set; }
    public HospitalPaymentQr? Qr { get; set; }
    public int HospitalId { get; set; }
    public int BillingId { get; set; }
    public int BillingPaymentTransactionId { get; set; }
    [Required, MaxLength(150)] public string GatewayPaymentId { get; set; } = "";
    // Razorpay QR payments need not have a Razorpay order ID.
    [MaxLength(150)] public string? GatewayOrderId { get; set; }
    // "Order" or "Qr". The corresponding foreign key must be populated.
    [Required, MaxLength(10)] public string PaymentSource { get; set; } = "Order";
    public decimal Amount { get; set; }
    public decimal RefundedAmount { get; set; }
    [Required, MaxLength(20)] public string Mode { get; set; } = "Test";
    [Required, MaxLength(30)] public string Status { get; set; } = "Captured";
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
}
