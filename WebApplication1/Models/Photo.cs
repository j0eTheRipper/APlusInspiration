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
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public int? Year { get; set; }
    public string? Description { get; set; }
    public string? ThumbPath { get; set; }
}
