using BackEnd.Controllers;
using BackEnd.Data;
using BackEnd.Models;
using BackEnd.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BackEnd.Tests.Controllers;

public class AuthControllerTests
{
    private readonly Mock<IUserRepository> _userRepositoryMock;
    private readonly AuthService _authService;
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        _userRepositoryMock = new Mock<IUserRepository>(); 

        var jwtSettings = Options.Create(new JwtSettings
        {
            Secret = "SUPER_SECRET_KEY_FOR_TESTS_1234567890_ABCDEF",
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            ExpiryMinutes = 60
        });
        _authService = new AuthService(jwtSettings);

        _controller = new AuthController(_userRepositoryMock.Object, _authService);
    }

    [Fact]
    public async Task Register_WhenEmailExists_ShouldReturnBadRequest()
    {
        var request = new RegisterRequest { NickName = "Ivan", Email = "exists@test.com", Password = "Password123" };
        
        _userRepositoryMock
            .Setup(repo => repo.GetByEmailAsync(request.Email))
            .ReturnsAsync(new User { Email = request.Email });

        var result = await _controller.Register(request);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Login_WithWrongPassword_ShouldReturnUnauthorized()
    {
        var request = new LoginRequest { Email = "test@test.com", Password = "WrongPassword" };
        var userInDb = new User 
        { 
            Email = "test@test.com", 
            PasswordHash = _authService.HashPassword("CorrectPassword") 
        };

        _userRepositoryMock
            .Setup(repo => repo.GetByEmailAsync(request.Email))
            .ReturnsAsync(userInDb);

        var result = await _controller.Login(request);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_ShouldReturnOkWithToken()
    {
        var request = new LoginRequest { Email = "test@test.com", Password = "CorrectPassword" };
        var userInDb = new User 
        { 
            Id = "123", 
            Email = "test@test.com", 
            PasswordHash = _authService.HashPassword("CorrectPassword") 
        };

        _userRepositoryMock
            .Setup(repo => repo.GetByEmailAsync(request.Email))
            .ReturnsAsync(userInDb);

        var result = await _controller.Login(request);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value;
        value.Should().NotBeNull();
        
        var tokenProperty = value!.GetType().GetProperty("token");
        tokenProperty.Should().NotBeNull();
        tokenProperty!.GetValue(value).Should().NotBeNull();
    }
}