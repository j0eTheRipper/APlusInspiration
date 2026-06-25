using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplication1.Models;

[Table("photos")]
public class Photo
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public string StoredPath { get; set; }
    public DateTime UploadedAt { get; set; }
    public int UploadedByUserId { get; set; }
}
