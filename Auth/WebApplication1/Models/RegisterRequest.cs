namespace BackEnd.Models;

public class RegisterRequest
{
    public string NickName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}