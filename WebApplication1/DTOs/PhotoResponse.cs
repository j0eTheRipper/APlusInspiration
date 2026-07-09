namespace WebApplication1.DTOs;

public class PhotoResponse
{
    public int Id { get; set; }
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public DateTime UploadedAt { get; set; }
    public string Url { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public int? Year { get; set; }
    public string? Description { get; set; }
    public string? ThumbUrl { get; set; }
}
