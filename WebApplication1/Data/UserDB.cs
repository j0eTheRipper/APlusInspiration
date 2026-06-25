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
}
