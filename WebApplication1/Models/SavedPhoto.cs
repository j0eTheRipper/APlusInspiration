using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplication1.Models;

[Table("saved_photos")]
public class SavedPhoto
{
    public int UserId { get; set; }
    public int PhotoId { get; set; }
    public DateTime SavedAt { get; set; }
}
