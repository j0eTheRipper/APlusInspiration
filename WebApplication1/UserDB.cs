using Microsoft.EntityFrameworkCore;

namespace WebApplication1;

public class UserDB: DbContext
{
    public UserDB(DbContextOptions<UserDB> options) : base(options)
    {
    }

    public DbSet<User> User => Set<User>();
}