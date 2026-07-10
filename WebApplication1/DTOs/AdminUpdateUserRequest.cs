namespace WebApplication1.DTOs;

public class AdminUpdateUserRequest
{
    public string Username { get; set; }
    public string Email { get; set; }
    public string Role { get; set; }
    public string? Password { get; set; }
}
