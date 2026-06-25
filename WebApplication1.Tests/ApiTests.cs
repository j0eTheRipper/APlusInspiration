using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using WebApplication1.Data;
using WebApplication1.DTOs;

namespace WebApplication1.Tests;

public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<UserDB>));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddDbContext<UserDB>(opt =>
                    opt.UseInMemoryDatabase("TestDb"));
            });
        });
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    private string GenerateTestToken(string role)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Name, "testuser"),
            new Claim(ClaimTypes.Role, role)
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("ThisIsASecureKeyForDevelopmentOnlyChangeInProduction123!"));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "WebApplication1",
            audience: "WebApplication1",
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Signup_ShouldReturnCreated()
    {
        var client = CreateClient();
        var req = new SignupRequest
        {
            Username = "newuser",
            Password = "pass123",
            Email = "new@example.com"
        };

        var response = await client.PostAsJsonAsync("/signup", req);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Signup_DuplicateUsername_ReturnsConflict()
    {
        var client = CreateClient();
        var req = new SignupRequest
        {
            Username = "dupeuser",
            Password = "pass123",
            Email = "dupe@example.com"
        };

        var first = await client.PostAsJsonAsync("/signup", req);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/signup", req);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Signup_MissingFields_ReturnsBadRequest()
    {
        var client = CreateClient();
        var req = new SignupRequest
        {
            Username = "",
            Password = "pass123",
            Email = "test@example.com"
        };

        var response = await client.PostAsJsonAsync("/signup", req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsToken()
    {
        var client = CreateClient();

        var signupReq = new SignupRequest
        {
            Username = "logintest",
            Password = "mypassword",
            Email = "login@example.com"
        };
        var signupRes = await client.PostAsJsonAsync("/signup", signupReq);
        Assert.Equal(HttpStatusCode.Created, signupRes.StatusCode);

        var loginReq = new LoginRequest
        {
            Username = "logintest",
            Password = "mypassword"
        };
        var loginRes = await client.PostAsJsonAsync("/login", loginReq);
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var body = await loginRes.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body.Token);
        Assert.Equal("logintest", body.Username);
        Assert.Equal("user", body.Role);
    }

    [Fact]
    public async Task Login_InvalidPassword_ReturnsUnauthorized()
    {
        var client = CreateClient();

        var signupReq = new SignupRequest
        {
            Username = "badpwd",
            Password = "correctpw",
            Email = "badpwd@example.com"
        };
        await client.PostAsJsonAsync("/signup", signupReq);

        var loginReq = new LoginRequest
        {
            Username = "badpwd",
            Password = "wrongpw"
        };
        var response = await client.PostAsJsonAsync("/login", loginReq);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_NonexistentUser_ReturnsUnauthorized()
    {
        var client = CreateClient();
        var loginReq = new LoginRequest
        {
            Username = "nobody",
            Password = "anything"
        };
        var response = await client.PostAsJsonAsync("/login", loginReq);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UploadPhoto_WithoutAuth_ReturnsUnauthorized()
    {
        var client = CreateClient();

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "test.jpg");

        var response = await client.PostAsync("/photos/upload", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UploadPhoto_AsAdmin_ReturnsCreated()
    {
        var client = CreateClient();
        var token = GenerateTestToken("admin");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "photo.jpg");

        var response = await client.PostAsync("/photos/upload", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<PhotoResponse>();
        Assert.NotNull(body);
        Assert.Equal("photo.jpg", body.FileName);
        Assert.NotEqual(0, body.Id);
    }

    [Fact]
    public async Task UploadPhoto_AsUser_ReturnsForbidden()
    {
        var client = CreateClient();
        var token = GenerateTestToken("user");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "photo.jpg");

        var response = await client.PostAsync("/photos/upload", content);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SetRole_WithoutAuth_ReturnsUnauthorized()
    {
        var client = CreateClient();
        var req = new SetRoleRequest { Role = "curator" };
        var response = await client.PostAsJsonAsync("/admin/users/1/role", req);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SetRole_AsAdmin_WithValidUser_ReturnsOk()
    {
        var client = CreateClient();

        var signupReq = new SignupRequest
        {
            Username = "targetuser",
            Password = "pass",
            Email = "target@example.com"
        };
        await client.PostAsJsonAsync("/signup", signupReq);

        var token = GenerateTestToken("admin");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var req = new SetRoleRequest { Role = "curator" };
        var response = await client.PostAsJsonAsync("/admin/users/1/role", req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("curator", body.GetProperty("role").GetString());
    }

    [Fact]
    public async Task SetRole_AsAdmin_ToInvalidRole_ReturnsBadRequest()
    {
        var client = CreateClient();
        var token = GenerateTestToken("admin");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var req = new SetRoleRequest { Role = "admin" };
        var response = await client.PostAsJsonAsync("/admin/users/1/role", req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetRole_AsCurator_ReturnsForbidden()
    {
        var client = CreateClient();
        var token = GenerateTestToken("curator");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var req = new SetRoleRequest { Role = "curator" };
        var response = await client.PostAsJsonAsync("/admin/users/1/role", req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetPhotos_Public_ReturnsOk()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/photos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPhotoById_NotFound_ReturnsNotFound()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/photos/999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
