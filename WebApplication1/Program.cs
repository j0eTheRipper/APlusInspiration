using Microsoft.EntityFrameworkCore;
using WebApplication1;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddDbContext<UserDB>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("PhotoAppDb")));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapPost("/signup", async (User user, UserDB db) =>
{
    db.User.Add(user);
    await db.SaveChangesAsync();
    return Results.Created();
});

app.MapPost("/login", (User user) =>
{
    return $"Hello, {user.username}";
});

app.Run();
