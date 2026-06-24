using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApplication1.Tests;

public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<UserDB>));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddDbContext<UserDB>(opt =>
                    opt.UseInMemoryDatabase("TestDb"));
            });
        }).CreateClient();
    }

    [Fact]
    public async Task Signup_ShouldReturnCreated()
    {
        var user = new User
        {
            username = "newuser",
            password = "pass123",
            email = "new@example.com"
        };

        var response = await _client.PostAsJsonAsync("/signup", user);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Login_ShouldReturnGreeting()
    {
        var user = new User
        {
            username = "testuser",
            password = "pass123",
            email = "test@example.com"
        };

        var response = await _client.PostAsJsonAsync("/login", user);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Hello, testuser", content);
    }
}
