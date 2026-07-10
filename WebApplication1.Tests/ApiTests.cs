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
using WebApplication1.Models;

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

    private string GenerateTestToken(string role, int userId = 1)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
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

    private async Task SeedUser(int id, string username = "testuser", string role = "user")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDB>();
        if (!await db.User.AnyAsync(u => u.Id == id))
        {
            db.User.Add(new User
            {
                Id = id,
                username = username,
                password = BCrypt.Net.BCrypt.HashPassword("pass"),
                email = $"{username}@example.com",
                role = role
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task SeedSubscription(int userId, string status = "active")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDB>();
        var existing = await db.Subscription.FirstOrDefaultAsync(s => s.UserId == userId);
        if (existing != null)
        {
            db.Subscription.Remove(existing);
            await db.SaveChangesAsync();
        }
        db.Subscription.Add(new Subscription
        {
            UserId = userId,
            StripeSubscriptionId = $"sub_test_{userId}",
            StripeCustomerId = $"cus_test_{userId}",
            Status = status,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> CreateTestPhoto(int userId = 1)
    {
        var adminClient = CreateClient();
        var adminToken = GenerateTestToken("admin", userId);
        adminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminToken);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "test.jpg");

        var response = await adminClient.PostAsync("/photos/upload", content);
        var body = await response.Content.ReadFromJsonAsync<PhotoResponse>();
        return body!.Id;
    }

    // ─── Original Auth Tests ──────────────────────────────────────────────────

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
        await SeedUser(1, "targetuser", "user");

        var client = CreateClient();
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

    // ─── Subscription-Gated Photos Tests ──────────────────────────────────────

    [Fact]
    public async Task GetPhotosByUser_NoSubscription_ReturnsForbidden()
    {
        var client = CreateClient();
        await SeedUser(50, "nosub_user");

        var response = await client.GetAsync("/photos/by/50");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetPhotosByUser_WithActiveSubscription_ReturnsOk()
    {
        var client = CreateClient();
        await SeedUser(51, "sub_user", "curator");
        await SeedSubscription(51, "active");
        var photoId = await CreateTestPhoto(51);

        var response = await client.GetAsync("/photos/by/51");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<List<PhotoResponse>>();
        Assert.NotNull(body);
        Assert.NotEmpty(body);
        Assert.Contains(body, p => p.Id == photoId);
    }

    [Fact]
    public async Task GetPhotosByUser_WithCanceledSubscription_ReturnsForbidden()
    {
        var client = CreateClient();
        await SeedUser(52, "canceled_user", "curator");
        await SeedSubscription(52, "canceled");

        var response = await client.GetAsync("/photos/by/52");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetPhotosByUser_WithExpiredSubscription_ReturnsForbidden()
    {
        var client = CreateClient();
        await SeedUser(53, "expired_user", "curator");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UserDB>();
            db.Subscription.Add(new Subscription
            {
                UserId = 53,
                StripeSubscriptionId = "sub_expired_53",
                StripeCustomerId = "cus_expired_53",
                Status = "active",
                CurrentPeriodEnd = DateTime.UtcNow.AddDays(-1),
                CreatedAt = DateTime.UtcNow.AddDays(-60),
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/photos/by/53");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetPhotosByUser_NoPhotos_WithSubscription_ReturnsEmptyList()
    {
        var client = CreateClient();
        await SeedUser(54, "empty_sub_user", "curator");
        await SeedSubscription(54, "active");

        var response = await client.GetAsync("/photos/by/54");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<List<PhotoResponse>>();
        Assert.NotNull(body);
        Assert.Empty(body);
    }

    // ─── Stripe Config Endpoint Tests ─────────────────────────────────────────

    [Fact]
    public async Task GetStripeConfig_ReturnsOk()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/stripe/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("publishableKey", out _));
        Assert.True(body.TryGetProperty("priceId", out _));
    }

    // ─── Subscribe Endpoint Tests ─────────────────────────────────────────────

    [Fact]
    public async Task Subscribe_WithoutAuth_ReturnsUnauthorized()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/subscribe", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Subscribe_AsUser_ReturnsForbidden()
    {
        var client = CreateClient();
        await SeedUser(60, "regular_sub_user");
        var token = GenerateTestToken("user", 60);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/subscribe", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Subscribe_AsCurator_ReturnsOkWithCheckoutUrl()
    {
        var client = CreateClient();
        await SeedUser(61, "curator_sub_user", "curator");
        var token = GenerateTestToken("curator", 61);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/subscribe", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("url", out var urlProp));
        Assert.Contains("checkout.stripe.com", urlProp.GetString());
    }

    [Fact]
    public async Task Subscribe_AsAdmin_ReturnsOkWithCheckoutUrl()
    {
        var client = CreateClient();
        await SeedUser(62, "admin_sub_user", "admin");
        var token = GenerateTestToken("admin", 62);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/subscribe", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("url", out var urlProp));
        Assert.Contains("checkout.stripe.com", urlProp.GetString());
    }

    // ─── Subscription Status Endpoint Tests ───────────────────────────────────

    [Fact]
    public async Task GetSubscription_WithoutAuth_ReturnsUnauthorized()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/subscription");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSubscription_AsCurator_NoSubscription_ReturnsNone()
    {
        var client = CreateClient();
        await SeedUser(70, "no_sub_curator", "curator");
        var token = GenerateTestToken("curator", 70);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/subscription");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("none", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetSubscription_AsCurator_WithSubscription_ReturnsStatus()
    {
        var client = CreateClient();
        await SeedUser(71, "active_curator", "curator");
        await SeedSubscription(71, "active");
        var token = GenerateTestToken("curator", 71);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/subscription");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("active", body.GetProperty("status").GetString());
    }

    // ─── Cancel Subscription Endpoint Tests ───────────────────────────────────

    [Fact]
    public async Task CancelSubscription_WithoutAuth_ReturnsUnauthorized()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/subscribe/cancel", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CancelSubscription_AsCurator_NoActive_ReturnsNotFound()
    {
        var client = CreateClient();
        await SeedUser(80, "cancel_no_sub", "curator");
        var token = GenerateTestToken("curator", 80);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/subscribe/cancel", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Embed Endpoint Tests ─────────────────────────────────────────────────

    [Fact]
    public async Task EmbedPage_NoSubscription_ReturnsUnavailableMessage()
    {
        var client = CreateClient();
        await SeedUser(90, "embed_no_sub");

        var response = await client.GetAsync("/embed/90");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Unavailable", html);
        Assert.Contains("does not have an active subscription", html);
    }

    [Fact]
    public async Task EmbedPage_WithActiveSubscription_ReturnsHtml()
    {
        var client = CreateClient();
        await SeedUser(91, "embed_active_sub", "curator");
        await SeedSubscription(91, "active");

        var response = await client.GetAsync("/embed/91");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("__EMBED_USER_ID__", html);
        Assert.Contains("91", html);
    }

    [Fact]
    public async Task EmbedPage_CanceledSubscription_ReturnsUnavailable()
    {
        var client = CreateClient();
        await SeedUser(92, "embed_canceled", "curator");
        await SeedSubscription(92, "canceled");

        var response = await client.GetAsync("/embed/92");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Unavailable", html);
    }

    // ─── Webhook Endpoint Tests ───────────────────────────────────────────────

    [Fact]
    public async Task WebhookEndpoint_WithInvalidSignature_ReturnsBadRequest()
    {
        var client = CreateClient();
        var req = new StringContent("{}", Encoding.UTF8, "application/json");
        req.Headers.Add("Stripe-Signature", "invalid_signature");

        var response = await client.PostAsync("/stripe/webhook", req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WebhookEndpoint_WithoutSignature_ReturnsBadRequest()
    {
        var client = CreateClient();
        var req = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/stripe/webhook", req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
