using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplication1;

[Table("users")]
public class User
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    public string username { get; set; }
    public string password { get; set; }
    public string email { get; set; }
}