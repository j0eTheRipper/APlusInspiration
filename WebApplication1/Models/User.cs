using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplication1.Models;

[Table("users")]
public class User
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    public string username { get; set; }
    public string password { get; set; }
    public string email { get; set; }
    public string role { get; set; } = "user";
    public string? StripeCustomerId { get; set; }
}
