namespace WebApplication1.Services;
public class PhotoService
{
    private readonly string _storagePath;
    public PhotoService(IConfiguration config)
    {
        _storagePath = config["PhotoStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        Directory.CreateDirectory(_storagePath);
    }

    public async Task<(string storedPath, string uniqueFileName)> SaveFileAsync(
        byte[] fileData, string originalFileName, string contentType)

    {
        var ext = Path.GetExtension(originalFileName);
        var uniqueName = $"{Guid.NewGuid()}{ext}";
        var storedPath = Path.Combine(_storagePath, uniqueName);

        await File.WriteAllBytesAsync(storedPath, fileData);


        return (storedPath, uniqueName);
    }


    public string GetFilePath(string storedPath)
    {
        return storedPath;
    }
}
