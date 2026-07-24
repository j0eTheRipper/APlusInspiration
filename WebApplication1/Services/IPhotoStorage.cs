namespace WebApplication1.Services;

public interface IPhotoStorage
{
    Task<(string storedPath, string? thumbPath, string uniqueFileName)> SaveFileAsync(
        byte[] fileData, string originalFileName, string contentType);
}
