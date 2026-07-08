namespace WebApplication1.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
public class PhotoService
{
    private readonly string _storagePath;
    private readonly string _thumbPath;
    private const int ThumbnailWidth = 600;
    public PhotoService(IConfiguration config)
    {
        _storagePath = config["PhotoStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        _thumbPath = Path.Combine(_storagePath, "thumbs");
        Directory.CreateDirectory(_storagePath);
        Directory.CreateDirectory(_thumbPath);
    }

    public async Task<(string storedPath, string uniqueFileName, string? thumbPath)> SaveFileAsync(
        byte[] fileData, string originalFileName, string contentType)

    {
        var ext = Path.GetExtension(originalFileName);
        var uniqueName = $"{Guid.NewGuid()}{ext}";
        var storedPath = Path.Combine(_storagePath, uniqueName);

        await File.WriteAllBytesAsync(storedPath, fileData);

         var thumbPath = await TryGenerateThumbnailAsync(fileData, uniqueName, contentType);

        return (storedPath, uniqueName, thumbPath);
    }

    private async Task<string?> TryGenerateThumbnailAsync(byte[] fileData, string uniqueName, string contentType)
    {
        if (contentType != null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var image = Image.Load(fileData);

            // Only downscale; never upscale smaller originals.
            if (image.Width > ThumbnailWidth)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(ThumbnailWidth, 0)
                }));
            }

            var thumbName = $"{Path.GetFileNameWithoutExtension(uniqueName)}.jpg";
            var thumbFullPath = Path.Combine(_thumbPath, thumbName);
            await image.SaveAsJpegAsync(thumbFullPath);
            return thumbFullPath;
        }
        catch
        {
            return null;
        }
    }

    public string GetFilePath(string storedPath)
    {
        return storedPath;
    }
}
