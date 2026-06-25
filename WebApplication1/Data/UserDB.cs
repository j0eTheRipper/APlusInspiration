using Microsoft.EntityFrameworkCore;
using WebApplication1.Models;

namespace WebApplication1.Data;

public class UserDB : DbContext
{
    public UserDB(DbContextOptions<UserDB> options) : base(options)
    {
    }

    public DbSet<User> User => Set<User>();
    public DbSet<Photo> Photo => Set<Photo>();
    public DbSet<SavedPhoto> SavedPhoto => Set<SavedPhoto>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SavedPhoto>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.PhotoId });
        });
    }
}
