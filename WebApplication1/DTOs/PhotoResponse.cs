namespace WebApplication1.DTOs;

public class PhotoResponse
{
    public int Id { get; set; }
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public DateTime UploadedAt { get; set; }
    public string Url { get; set; }
}
