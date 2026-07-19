namespace WebApplication1.DTOs;

public record ColorUpdateRequest(string S3Key, string HexColor, double Hue);