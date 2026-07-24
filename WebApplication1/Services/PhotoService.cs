using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace WebApplication1.Services;

public class PhotoService : IPhotoStorage
{
    private const int ThumbWidth = 600;

    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly string _publicUrlBase;

    public PhotoService(IAmazonS3 s3Client, IConfiguration config)
    {
        _s3Client = s3Client;
        _bucketName = config["PhotoStorage:S3BucketName"]!;
        var region = config["PhotoStorage:S3Region"] ?? "us-east-1";
        _publicUrlBase = $"https://{_bucketName}.s3.{region}.amazonaws.com";
    }

    public async Task<(string storedPath, string? thumbPath, string uniqueFileName)> SaveFileAsync(
        byte[] fileData, string originalFileName, string contentType)
    {
        var ext = Path.GetExtension(originalFileName);
        var uniqueName = $"{Guid.NewGuid()}{ext}";
        var originalKey = $"uploads/{uniqueName}";

        using (var stream = new MemoryStream(fileData))
        {
            await _s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = originalKey,
                InputStream = stream,
                ContentType = contentType
            });
        }

        string? thumbUrl = null;
        try
        {
            var thumbKey = $"uploads/thumbs/{Path.GetFileNameWithoutExtension(uniqueName)}.jpg";

            using var image = Image.Load(fileData);
            image.Mutate(x => x.Resize(ThumbWidth, 0));

            using var thumbStream = new MemoryStream();
            await image.SaveAsJpegAsync(thumbStream);
            thumbStream.Position = 0;

            await _s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = thumbKey,
                InputStream = thumbStream,
                ContentType = "image/jpeg"
            });

            thumbUrl = $"{_publicUrlBase}/{thumbKey}";
        }
        catch (Exception)
        {
            // keep the original and leave ThumbPath null so the reader falls back to the full-res file.
        }

        var storedUrl = $"{_publicUrlBase}/{originalKey}";
        return (storedUrl, thumbUrl, uniqueName);
    }
}
