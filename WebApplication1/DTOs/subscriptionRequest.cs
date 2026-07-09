using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebApplication1.DTOs;

public class SubscriptionRequest
{
    [Required(ErrorMessage = "PaymentMethodId is required.")]
    [JsonPropertyName("paymentMethodId")]
    public string PaymentMethodId { get; set; }
}