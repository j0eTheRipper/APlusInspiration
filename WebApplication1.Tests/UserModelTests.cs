namespace WebApplication1.Tests;

public class UserModelTests
{
    [Fact]
    public void User_ShouldHaveDefaultValues()
    {
        var user = new User();

        Assert.Equal(0, user.Id);
        Assert.Null(user.username);
        Assert.Null(user.password);
        Assert.Null(user.email);
    }

    [Fact]
    public void User_ShouldSetProperties()
    {
        var user = new User
        {
            Id = 1,
            username = "testuser",
            password = "password123",
            email = "test@example.com"
        };

        Assert.Equal(1, user.Id);
        Assert.Equal("testuser", user.username);
        Assert.Equal("password123", user.password);
        Assert.Equal("test@example.com", user.email);
    }
}
