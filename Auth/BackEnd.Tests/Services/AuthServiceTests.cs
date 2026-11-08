using BackEnd.Data;
using BackEnd.Models;
using BackEnd.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BackEnd.Tests.Services;

public class AuthServiceTests
{
    private readonly AuthService _authService;

    public AuthServiceTests()
    {
        var jwtSettings = Options.Create(new JwtSettings
        {
            Secret = "SUPER_SECRET_KEY_FOR_TESTS_1234567890_ABCDEF",
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            ExpiryMinutes = 60
        });

        _authService = new AuthService(jwtSettings);
    }

    [Fact]
    public void HashPassword_ShouldReturnValidHash()
    {
        var password = "MySecretPassword123";

        var hash = _authService.HashPassword(password);

        hash.Should().NotBeNullOrEmpty();
        hash.Should().NotBe(password);
        hash.Should().StartWith("$2");
    }

    [Fact]
    public void VerifyPassword_WithCorrectPassword_ShouldReturnTrue()
    {
        var password = "MySecretPassword123";
        var hash = _authService.HashPassword(password);

        var result = _authService.VerifyPassword(password, hash);

        result.Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_WithWrongPassword_ShouldReturnFalse()
    {
        var password = "MySecretPassword123";
        var hash = _authService.HashPassword(password);

        var result = _authService.VerifyPassword("WrongPassword", hash);

        result.Should().BeFalse();
    }

    [Fact]
    public void GenerateJwtToken_ShouldReturnValidToken()
    {
        var user = new User { Id = "1234567890", Email = "test@test.com" };

        var token = _authService.GenerateJwtToken(user);

        token.Should().NotBeNullOrEmpty();
        token.Should().Contain(".");
        token.Split('.').Length.Should().Be(3); 
    }
}