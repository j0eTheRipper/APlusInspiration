using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace WebApplication1.Services;

public class PhotoService
{
    private const int ThumbWidth = 600; 

    private readonly string _storagePath;
    private readonly string _thumbPath;

    public PhotoService(IConfiguration config)
    {
        _storagePath = config["PhotoStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        _thumbPath = Path.Combine(_storagePath, "thumbs");
        Directory.CreateDirectory(_storagePath);
        Directory.CreateDirectory(_thumbPath);
    }

    public async Task<(string storedPath, string? thumbPath, string uniqueFileName)> SaveFileAsync(
        byte[] fileData, string originalFileName, string contentType)
    {
        var ext = Path.GetExtension(originalFileName);
        var uniqueName = $"{Guid.NewGuid()}{ext}";
        var storedPath = Path.Combine(_storagePath, uniqueName);

        await File.WriteAllBytesAsync(storedPath, fileData);

        string? thumbPath = null;
        try
        {
        
            var thumbName = $"{Path.GetFileNameWithoutExtension(uniqueName)}.jpg";
            var candidate = Path.Combine(_thumbPath, thumbName);

            using var image = Image.Load(fileData);
            image.Mutate(x => x.Resize(ThumbWidth, 0));
            await image.SaveAsJpegAsync(candidate);

            thumbPath = candidate;
        }
        catch (Exception)
        {
            // keep the original and leave ThumbPath null so the reader falls back to the full-res file.
        }

        return (storedPath, thumbPath, uniqueName);
    }

    public string GetFilePath(string storedPath)
    {
        return storedPath;
    }
}
