using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebApplication1.DTOs;

public class VerifyRequest
{
    [Required(ErrorMessage = "SessionId is required.")]
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; }
}
