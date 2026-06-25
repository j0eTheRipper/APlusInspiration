using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using WebApplication1.Data;
using WebApplication1.DTOs;
using WebApplication1.Models;
using WebApplication1.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDbContext<UserDB>(opt =>
        opt.UseInMemoryDatabase("TestDb"));
}
else
{
    builder.Services.AddDbContext<UserDB>(opt =>
        opt.UseSqlServer(builder.Configuration.GetConnectionString("PhotoAppDb")));
}
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<PhotoService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireRole("admin"));
    options.AddPolicy("CuratorOrAdmin", policy =>
        policy.RequireRole("curator", "admin"));
});

var app = builder.Build();

try
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<UserDB>();
        if (db.Database.IsInMemory())
            db.Database.EnsureCreated();
        else
            db.Database.Migrate();
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Database initialization skipped: {ex.Message}");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// ─── Signup ─────────────────────────────────────────────────────────────────

app.MapPost("/signup", async (SignupRequest req, UserDB db) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) ||
        string.IsNullOrWhiteSpace(req.Password) ||
        string.IsNullOrWhiteSpace(req.Email))
        return Results.BadRequest("Username, password, and email are required.");

    if (await db.User.AnyAsync(u => u.username == req.Username))
        return Results.Conflict("Username already exists.");

    if (await db.User.AnyAsync(u => u.email == req.Email))
        return Results.Conflict("Email already exists.");

    var user = new User
    {
        username = req.Username,
        password = BCrypt.Net.BCrypt.HashPassword(req.Password),
        email = req.Email,
        role = "user"
    };

    db.User.Add(user);
    await db.SaveChangesAsync();

    return Results.Created();
});

// ─── Login ──────────────────────────────────────────────────────────────────

app.MapPost("/login", async (LoginRequest req, UserDB db, TokenService tokenService) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) ||
        string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest("Username and password are required.");

    var user = await db.User.FirstOrDefaultAsync(u => u.username == req.Username);
    if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.password))
        return Results.Unauthorized();

    var token = tokenService.GenerateToken(user.Id, user.username, user.role);
    return Results.Ok(new LoginResponse
    {
        Token = token,
        Username = user.username,
        Role = user.role
    });
});

// ─── Upload Photo ───────────────────────────────────────────────────────────

app.MapPost("/photos/upload", async (HttpRequest request, UserDB db, PhotoService photoService) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest("Request must be multipart/form-data.");

    var file = request.Form.Files.FirstOrDefault();
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file provided.");

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var fileData = ms.ToArray();

    var (storedPath, uniqueFileName) = await photoService.SaveFileAsync(fileData, file.FileName);

    var userIdClaim = request.HttpContext.User.FindFirst(
        System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    var photo = new Photo
    {
        FileName = file.FileName,
        ContentType = file.ContentType ?? "application/octet-stream",
        StoredPath = storedPath,
        UploadedAt = DateTime.UtcNow,
        UploadedByUserId = int.Parse(userIdClaim!)
    };

    db.Photo.Add(photo);
    await db.SaveChangesAsync();

    var baseUrl = $"{request.Scheme}://{request.Host}";
    return Results.Created($"/photos/{photo.Id}", new PhotoResponse
    {
        Id = photo.Id,
        FileName = photo.FileName,
        ContentType = photo.ContentType,
        UploadedAt = photo.UploadedAt,
        Url = $"{baseUrl}/photos/{photo.Id}/file"
    });
}).RequireAuthorization("CuratorOrAdmin");

// ─── List Photos ────────────────────────────────────────────────────────────

app.MapGet("/photos", async (UserDB db) =>
{
    var photos = await db.Photo
        .OrderByDescending(p => p.UploadedAt)
        .Select(p => new PhotoResponse
        {
            Id = p.Id,
            FileName = p.FileName,
            ContentType = p.ContentType,
            UploadedAt = p.UploadedAt,
            Url = $"/photos/{p.Id}/file"
        })
        .ToListAsync();

    return Results.Ok(photos);
});

// ─── Get Photo Metadata ─────────────────────────────────────────────────────

app.MapGet("/photos/{id:int}", async (int id, UserDB db) =>
{
    var photo = await db.Photo.FindAsync(id);
    if (photo == null)
        return Results.NotFound();

    return Results.Ok(new PhotoResponse
    {
        Id = photo.Id,
        FileName = photo.FileName,
        ContentType = photo.ContentType,
        UploadedAt = photo.UploadedAt,
        Url = $"/photos/{photo.Id}/file"
    });
});

// ─── Serve Photo File ───────────────────────────────────────────────────────

app.MapGet("/photos/{id:int}/file", async (int id, UserDB db, PhotoService photoService) =>
{
    var photo = await db.Photo.FindAsync(id);
    if (photo == null)
        return Results.NotFound();

    return Results.File(
        File.OpenRead(photo.StoredPath),
        photo.ContentType,
        photo.FileName);
});

// ─── Save Photo ─────────────────────────────────────────────────────────────

app.MapPut("/photo/{id:int}", async (int id, HttpRequest request, UserDB db) =>
{
    var userIdClaim = request.HttpContext.User.FindFirst(
        System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (userIdClaim == null)
        return Results.Unauthorized();

    var photo = await db.Photo.FindAsync(id);
    if (photo == null)
        return Results.NotFound("Photo not found.");

    var userId = int.Parse(userIdClaim);

    var existing = await db.SavedPhoto.FindAsync(userId, id);
    if (existing != null)
        return Results.Ok(new SavedPhotoResponse
        {
            UserId = existing.UserId,
            PhotoId = existing.PhotoId,
            SavedAt = existing.SavedAt
        });

    var saved = new SavedPhoto
    {
        UserId = userId,
        PhotoId = id,
        SavedAt = DateTime.UtcNow
    };

    db.SavedPhoto.Add(saved);
    await db.SaveChangesAsync();

    return Results.Created($"/saved/{saved.UserId}/{saved.PhotoId}", new SavedPhotoResponse
    {
        UserId = saved.UserId,
        PhotoId = saved.PhotoId,
        SavedAt = saved.SavedAt
    });
});

// ─── Unsave Photo ───────────────────────────────────────────────────────────

app.MapDelete("/saved/{id:int}", async (int id, HttpRequest request, UserDB db) =>
{
    var userIdClaim = request.HttpContext.User.FindFirst(
        System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (userIdClaim == null)
        return Results.Unauthorized();

    var userId = int.Parse(userIdClaim);

    var saved = await db.SavedPhoto.FindAsync(userId, id);
    if (saved == null)
        return Results.NotFound("Save record not found.");

    db.SavedPhoto.Remove(saved);
    await db.SaveChangesAsync();

    return Results.NoContent();
});

// ─── Set User Role (Admin only) ─────────────────────────────────────────────

app.MapPost("/admin/users/{id:int}/role", async (int id, SetRoleRequest req, UserDB db) =>
{
    var validRoles = new[] { "user", "curator" };
    if (!validRoles.Contains(req.Role))
        return Results.BadRequest("Role must be 'user' or 'curator'.");

    var user = await db.User.FindAsync(id);
    if (user == null)
        return Results.NotFound("User not found.");

    user.role = req.Role;
    await db.SaveChangesAsync();

    return Results.Ok(new { user.Id, user.username, user.role });
}).RequireAuthorization("AdminOnly");

app.Run();
