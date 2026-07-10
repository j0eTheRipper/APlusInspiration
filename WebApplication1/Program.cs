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

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:5173", "http://localhost:4173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

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
builder.Services.AddScoped<StripeService>();

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
                Encoding.UTF8.GetBytes(
                    builder.Configuration["Jwt:Key"] ?? "DevelopmentKeyThatIsLongEnoughForHmac256!"))
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

app.UseCors();
app.UseStaticFiles();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// ─── Helpers ─────────────────────────────────────────────────────────────────

static async Task<bool> HasActiveSubscription(UserDB db, int userId)
{
    var activeStatuses = new[] { "active", "trialing", "past_due" };
    return await db.Subscription.AnyAsync(s =>
        s.UserId == userId &&
        activeStatuses.Contains(s.Status) &&
        s.CurrentPeriodEnd > DateTime.UtcNow);
}

// ─── Auth Endpoints ──────────────────────────────────────────────────────────

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

// ─── Photo Endpoints ─────────────────────────────────────────────────────────

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

    var contentType = file.ContentType ?? "application/octet-stream";

    var (storedPath, thumbPath, uniqueFileName) =
        await photoService.SaveFileAsync(fileData, file.FileName, contentType);

    var userIdClaim = request.HttpContext.User.FindFirst(
        System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    string? Field(string key) =>
        request.Form.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : null;

    int? FieldInt(string key) =>
        request.Form.TryGetValue(key, out var v) && int.TryParse(v.ToString(), out var n)
            ? n
            : null;


    var photo = new Photo
    {
        FileName = file.FileName,
        ContentType = contentType,
        StoredPath = storedPath,
        ThumbPath = thumbPath,
        UploadedAt = DateTime.UtcNow,
        UploadedByUserId = int.Parse(userIdClaim!),
        Title = Field("title"),
        Artist = Field("artist"),
        Year = FieldInt("year"),
        Description = Field("description")
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
        Url = $"{baseUrl}/photos/{photo.Id}/file",
        ThumbUrl = photo.ThumbPath != null ? $"{baseUrl}/photos/{photo.Id}/thumb" : null,
        Title = photo.Title,
        Artist = photo.Artist,
        Year = photo.Year,
        Description = photo.Description
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
            Url = $"/photos/{p.Id}/file",
            ThumbUrl = p.ThumbPath != null ? $"/photos/{p.Id}/thumb" : null,
            Title = p.Title,
            Artist = p.Artist,
            Year = p.Year,
            Description = p.Description
        })
        .ToListAsync();

    return Results.Ok(photos);
});

// ─── My Photos (for upload page) ──────────────────────────────────────────

app.MapGet("/photos/mine", async (HttpRequest request, UserDB db) =>
{
    var userIdClaim = request.HttpContext.User.FindFirst(
        System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (userIdClaim == null)
        return Results.Unauthorized();

    var userId = int.Parse(userIdClaim);
    var isAdmin = request.HttpContext.User.IsInRole("admin");

    var query = db.Photo.AsQueryable();
    if (!isAdmin)
        query = query.Where(p => p.UploadedByUserId == userId);

    var photos = await query
        .OrderByDescending(p => p.UploadedAt)
        .Select(p => new PhotoResponse
        {
            Id = p.Id,
            FileName = p.FileName,
            ContentType = p.ContentType,
            UploadedAt = p.UploadedAt,
            Url = $"/photos/{p.Id}/file",
            ThumbUrl = p.ThumbPath != null ? $"/photos/{p.Id}/thumb" : null,
            Title = p.Title,
            Artist = p.Artist,
            Year = p.Year,
            Description = p.Description
        })
        .ToListAsync();

    return Results.Ok(photos);
}).RequireAuthorization("CuratorOrAdmin");

// ─── Photos by User (subscription-gated) ────────────────────────────────────

app.MapGet("/photos/by/{userId:int}", async (int userId, UserDB db) =>
{
    if (!await HasActiveSubscription(db, userId))
        return Results.Forbid();

    var photos = await db.Photo
        .Where(p => p.UploadedByUserId == userId)
        .OrderByDescending(p => p.UploadedAt)
        .Select(p => new PhotoResponse
        {
            Id = p.Id,
            FileName = p.FileName,
            ContentType = p.ContentType,
            UploadedAt = p.UploadedAt,
            Url = $"/photos/{p.Id}/file",
            ThumbUrl = p.ThumbPath != null ? $"/photos/{p.Id}/thumb" : null,
            Title = p.Title,
            Artist = p.Artist,
            Year = p.Year,
            Description = p.Description
        })
        .ToListAsync();

    return Results.Ok(photos);
});

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

// ─── Serve Photo Thumbnail ──────────────────────────────────────────────────

app.MapGet("/photos/{id:int}/thumb", async (int id, UserDB db) =>
{
    var photo = await db.Photo.FindAsync(id);
    if (photo == null)
        return Results.NotFound();

    // Fall back to the original image if thumbnail doesn't exist
    var path = photo.ThumbPath ?? photo.StoredPath;
    if (!File.Exists(path))
        return Results.NotFound();

    var contentType = photo.ThumbPath != null ? "image/jpeg" : photo.ContentType;
    return Results.File(File.OpenRead(path), contentType);
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

// ─── Admin Endpoints ────────────────────────────────────────────────────────

app.MapGet("/admin/users", async (UserDB db) =>
{
    var users = await db.User
        .Select(u => new { u.Id, u.username, u.email, u.role })
        .ToListAsync();
    return Results.Ok(users);
}).RequireAuthorization("AdminOnly");

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

app.MapPost("/admin/users", async (AdminCreateUserRequest req, UserDB db) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) ||
        string.IsNullOrWhiteSpace(req.Password) ||
        string.IsNullOrWhiteSpace(req.Email))
        return Results.BadRequest("Username, password, and email are required.");

    var validRoles = new[] { "user", "curator" };
    var role = string.IsNullOrWhiteSpace(req.Role) ? "user" : req.Role;
    if (!validRoles.Contains(role))
        return Results.BadRequest("Role must be 'user' or 'curator'.");

    if (await db.User.AnyAsync(u => u.username == req.Username))
        return Results.Conflict("Username already exists.");
    if (await db.User.AnyAsync(u => u.email == req.Email))
        return Results.Conflict("Email already exists.");

    var user = new User
    {
        username = req.Username,
        email = req.Email,
        password = BCrypt.Net.BCrypt.HashPassword(req.Password),
        role = role
    };

    db.User.Add(user);
    await db.SaveChangesAsync();

    return Results.Created($"/admin/users/{user.Id}",
        new { user.Id, user.username, user.email, user.role });
}).RequireAuthorization("AdminOnly");

app.MapPut("/admin/users/{id:int}", async (int id, AdminUpdateUserRequest req, UserDB db) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Email))
        return Results.BadRequest("Username and email are required.");

    var validRoles = new[] { "user", "curator" };
    if (!validRoles.Contains(req.Role))
        return Results.BadRequest("Role must be 'user' or 'curator'.");

    var user = await db.User.FindAsync(id);
    if (user == null)
        return Results.NotFound("User not found.");

    // Admin accounts are read-only from this API.
    if (user.role == "admin")
        return Results.BadRequest("Admin users cannot be modified.");

    // Uniqueness excluding self.
    if (await db.User.AnyAsync(u => u.username == req.Username && u.Id != id))
        return Results.Conflict("Username already exists.");
    if (await db.User.AnyAsync(u => u.email == req.Email && u.Id != id))
        return Results.Conflict("Email already exists.");

    user.username = req.Username;
    user.email = req.Email;
    user.role = req.Role;
    if (!string.IsNullOrWhiteSpace(req.Password))
        user.password = BCrypt.Net.BCrypt.HashPassword(req.Password);

    await db.SaveChangesAsync();

    return Results.Ok(new { user.Id, user.username, user.email, user.role });
}).RequireAuthorization("AdminOnly");

app.MapDelete("/admin/users/{id:int}", async (int id, UserDB db) =>
{
    var user = await db.User.FindAsync(id);
    if (user == null)
        return Results.NotFound("User not found.");

    // Admin accounts cannot be deleted from this API.
    if (user.role == "admin")
        return Results.BadRequest("Admin users cannot be deleted.");

    // Photos this user uploaded (no FK -> manual cleanup).
    var photos = await db.Photo.Where(p => p.UploadedByUserId == id).ToListAsync();
    var photoIds = photos.Select(p => p.Id).ToList();

    // SavedPhoto rows owned by this user OR pointing at this user's photos (no FK -> manual).
    var savedRows = await db.SavedPhoto
        .Where(s => s.UserId == id || photoIds.Contains(s.PhotoId))
        .ToListAsync();

    // Subscription has a cascade FK, but remove explicitly so this also works
    // under the InMemory test provider (which does not enforce cascade).
    var subs = await db.Subscription.Where(s => s.UserId == id).ToListAsync();

    db.SavedPhoto.RemoveRange(savedRows);
    db.Photo.RemoveRange(photos);
    db.Subscription.RemoveRange(subs);
    db.User.Remove(user);
    await db.SaveChangesAsync();

    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

// ─── Stripe Endpoints ───────────────────────────────────────────────────────

app.MapGet("/stripe/config", (IConfiguration config) =>
{
    return Results.Ok(new
    {
        publishableKey = config["Stripe:PublishableKey"],
        priceId = config["Stripe:PriceId"]
    });
});

app.MapPost("/subscribe", async (HttpRequest request, UserDB db, StripeService stripeService, IConfiguration config) =>
{
    var userIdClaim = request.HttpContext.User.FindFirst(
        System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (userIdClaim == null)
        return Results.Unauthorized();

    var userId = int.Parse(userIdClaim);
    var user = await db.User.FindAsync(userId);
    if (user == null)
        return Results.NotFound("User not found.");

    var frontendUrl = config["Frontend:Url"] ?? "http://localhost:5173";
    var successUrl = $"{frontendUrl}/subscribe?session_id={{CHECKOUT_SESSION_ID}}";
    var cancelUrl = $"{frontendUrl}/subscribe?canceled=true";

    try
    {
        var checkoutUrl = await stripeService.CreateCheckoutSessionAsync(user, successUrl, cancelUrl);
        return Results.Ok(new { url = checkoutUrl });
    }
    catch (Stripe.StripeException ex)
    {
        return Results.BadRequest($"Stripe error: {ex.Message}");
    }
}).RequireAuthorization("CuratorOrAdmin");

app.MapPost("/subscribe/verify", async (HttpRequest request, UserDB db, StripeService stripeService) =>
{
    var userIdClaim = request.HttpContext.User.FindFirst(
        System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (userIdClaim == null)
        return Results.Unauthorized();

    var userId = int.Parse(userIdClaim);
    var body = await request.ReadFromJsonAsync<VerifyRequest>();
    if (body == null || string.IsNullOrWhiteSpace(body.SessionId))
        return Results.BadRequest("sessionId is required.");

    try
    {
        var subscription = await stripeService.VerifyCheckoutSessionAsync(userId, body.SessionId);
        if (subscription == null)
            return Results.BadRequest("Invalid or incomplete checkout session.");

        return Results.Ok(new
        {
            status = subscription.Status,
            currentPeriodEnd = subscription.CurrentPeriodEnd,
        });
    }
    catch (Stripe.StripeException ex)
    {
        return Results.BadRequest($"Stripe error: {ex.Message}");
    }
}).RequireAuthorization("CuratorOrAdmin");

app.MapGet("/subscription", async (HttpRequest request, UserDB db) =>
{
    var userIdClaim = request.HttpContext.User.FindFirst(
        System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (userIdClaim == null)
        return Results.Unauthorized();

    var userId = int.Parse(userIdClaim);
    var subscription = await db.Subscription
        .Where(s => s.UserId == userId)
        .OrderByDescending(s => s.CreatedAt)
        .Select(s => new
        {
            s.Id,
            s.Status,
            s.CurrentPeriodEnd,
            s.CreatedAt,
            s.StripeSubscriptionId
        })
        .FirstOrDefaultAsync();

    if (subscription == null)
        return Results.Ok(new { status = "none" });

    return Results.Ok(subscription);
}).RequireAuthorization("CuratorOrAdmin");

app.MapPost("/subscribe/cancel", async (HttpRequest request, UserDB db, StripeService stripeService) =>
{
    var userIdClaim = request.HttpContext.User.FindFirst(
        System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (userIdClaim == null)
        return Results.Unauthorized();

    var userId = int.Parse(userIdClaim);
    var subscription = await db.Subscription
        .FirstOrDefaultAsync(s => s.UserId == userId && s.Status == "active");

    if (subscription == null)
        return Results.NotFound("No active subscription found.");

    await stripeService.CancelSubscriptionAsync(subscription.StripeSubscriptionId);
    return Results.Ok(new { message = "Subscription canceled." });
}).RequireAuthorization("CuratorOrAdmin");

app.MapPost("/stripe/webhook", async (HttpRequest request, IConfiguration config, StripeService stripeService) =>
{
    var json = await new StreamReader(request.Body).ReadToEndAsync();
    var signatureHeader = request.Headers["Stripe-Signature"].FirstOrDefault() ?? "";
    var webhookSecret = config["Stripe:WebhookSecret"]!;

    try
    {
        await stripeService.HandleWebhookAsync(json, signatureHeader, webhookSecret);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Webhook error: {ex.Message}");
    }
});

// ─── Embed Page ─────────────────────────────────────────────────────────────

app.MapGet("/embed/{userId:int}", async (int userId, UserDB db, IWebHostEnvironment env) =>
{
    if (!await HasActiveSubscription(db, userId))
    {
        var unavailableHtml = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"><title>Gallery</title></head><body>" +
            "<div style=\"display:flex;align-items:center;justify-content:center;height:100vh;background:#060608;color:#fff;font-family:system-ui,sans-serif;text-align:center;padding:20px;\">" +
            "<div><h1 style=\"font-size:24px;font-weight:300;margin:0 0 12px;\">Unavailable</h1>" +
            "<p style=\"color:#8a8a93;font-size:14px;\">This curator does not have an active subscription.</p></div></div></body></html>";
        return Results.Content(unavailableHtml, "text/html");
    }

    var embedPath = Path.Combine(env.WebRootPath, "embed", "embed.html");
    if (!System.IO.File.Exists(embedPath))
        return Results.NotFound("Embed not available.");

    var html = await System.IO.File.ReadAllTextAsync(embedPath);
    // Rewrite absolute asset paths to include the /embed prefix so they resolve
    // correctly when the HTML is served under /embed/{userId}.
    html = html.Replace("src=\"/assets/", "src=\"/embed/assets/");
    html = html.Replace("href=\"/assets/", "href=\"/embed/assets/");
    var injected = html.Replace(
        "</head>",
        $"<script>window.__EMBED_USER_ID__ = {userId};</script></head>");

    return Results.Content(injected, "text/html");
});

app.MapFallbackToFile("index.html");
app.Run();
