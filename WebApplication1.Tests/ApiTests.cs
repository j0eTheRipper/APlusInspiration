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
using Xunit;
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
            builder.UseSetting("Environment", "Testing");
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

    [Fact]
    public async Task SavePhoto_WithoutAuth_ReturnsUnauthorized()
    {
        var client = CreateClient();
        var response = await client.PutAsync("/photo/1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SavePhoto_NonExistentPhoto_ReturnsNotFound()
    {
        var client = CreateClient();
        var token = GenerateTestToken("user");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PutAsync("/photo/999", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SavePhoto_AsUser_ReturnsCreated()
    {
        var client = CreateClient();
        var photoId = await CreateTestPhoto();

        var token = GenerateTestToken("user");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PutAsync($"/photo/{photoId}", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SavedPhotoResponse>();
        Assert.NotNull(body);
        Assert.Equal(photoId, body.PhotoId);
        Assert.Equal(1, body.UserId);
    }

    [Fact]
    public async Task SavePhoto_AsCurator_ReturnsCreated()
    {
        var client = CreateClient();
        var photoId = await CreateTestPhoto();

        var token = GenerateTestToken("curator");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PutAsync($"/photo/{photoId}", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SavePhoto_AsAdmin_ReturnsCreated()
    {
        var client = CreateClient();
        var photoId = await CreateTestPhoto();

        var token = GenerateTestToken("admin");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PutAsync($"/photo/{photoId}", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SavePhoto_DuplicateSave_ReturnsOk()
    {
        var client = CreateClient();
        var photoId = await CreateTestPhoto();

        var token = GenerateTestToken("user");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var first = await client.PutAsync($"/photo/{photoId}", null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PutAsync($"/photo/{photoId}", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<SavedPhotoResponse>();
        Assert.NotNull(body);
        Assert.Equal(photoId, body.PhotoId);
        Assert.Equal(1, body.UserId);
    }

    [Fact]
    public async Task UnsavePhoto_WithoutAuth_ReturnsUnauthorized()
    {
        var client = CreateClient();
        var response = await client.DeleteAsync("/saved/1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnsavePhoto_NotSaved_ReturnsNotFound()
    {
        var client = CreateClient();
        var token = GenerateTestToken("user");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.DeleteAsync("/saved/999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnsavePhoto_AsUser_ReturnsNoContent()
    {
        var client = CreateClient();
        var photoId = await CreateTestPhoto();

        var token = GenerateTestToken("user");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var saveResponse = await client.PutAsync($"/photo/{photoId}", null);
        Assert.Equal(HttpStatusCode.Created, saveResponse.StatusCode);

        var response = await client.DeleteAsync($"/saved/{photoId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task UnsavePhoto_AlreadyUnsaved_ReturnsNotFound()
    {
        var client = CreateClient();
        var photoId = await CreateTestPhoto();

        var token = GenerateTestToken("user");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var saveResponse = await client.PutAsync($"/photo/{photoId}", null);
        Assert.Equal(HttpStatusCode.Created, saveResponse.StatusCode);

        await client.DeleteAsync($"/saved/{photoId}");

        var secondDelete = await client.DeleteAsync($"/saved/{photoId}");
        Assert.Equal(HttpStatusCode.NotFound, secondDelete.StatusCode);
    }

    private async Task<int> CreateTestPhoto()
    {
        var adminClient = CreateClient();
        var adminToken = GenerateTestToken("admin");
        adminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminToken);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "test.jpg");

        var response = await adminClient.PostAsync("/photos/upload", content);
        var body = await response.Content.ReadFromJsonAsync<PhotoResponse>();
        return body!.Id;
    }
}
