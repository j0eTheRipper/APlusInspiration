using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplication1.Models;

[Table("subscriptions")]
public class Subscription
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    public int UserId { get; set; }
    public string StripeSubscriptionId { get; set; }
    public string StripeCustomerId { get; set; }
    public string Status { get; set; }
    public DateTime CurrentPeriodEnd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; }
}