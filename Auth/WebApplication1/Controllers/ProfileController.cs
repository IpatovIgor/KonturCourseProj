using System.IdentityModel.Tokens.Jwt;
using BackEnd.Data;
using BackEnd.Models;
using BackEnd.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackEnd.Controllers;

[ApiController]
[Authorize]
[Route("api/profile/summary-settings")]
public sealed class ProfileController(HabitService habits, IUserRepository users) : ControllerBase
{
    [HttpGet]
    [NotificationSettingsEndpoint]
    public async Task<NotificationSettings> Get(CancellationToken ct)
    {
        var owner = await habits.GetOwnerAsync(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, ct);
        return new(owner.SummaryEnabled, owner.SummaryLocalTime);
    }

    [HttpPut]
    [NotificationSettingsEndpoint]
    public async Task<NotificationSettings> Put(NotificationSettingsRequest request, CancellationToken ct)
    {
        var owner = await habits.GetOwnerAsync(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, ct);
        var saved = await users.SaveSummaryAsync(owner.Id!, request.Validate(), ct)
            ?? throw HabitApiException.Unauthenticated();
        return new(saved.SummaryEnabled, saved.SummaryLocalTime);
    }
}
