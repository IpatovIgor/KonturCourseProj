using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;

namespace HabitTracker.Notifications.Hubs;

public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => GetUserId(connection.User);

    public static string? GetUserId(ClaimsPrincipal? principal)
    {
        var subjects = principal?.FindAll(JwtRegisteredClaimNames.Sub).ToArray();
        return subjects is { Length: 1 } && ObjectId.TryParse(subjects[0].Value, out var id)
            ? id.ToString() : null;
    }
}
