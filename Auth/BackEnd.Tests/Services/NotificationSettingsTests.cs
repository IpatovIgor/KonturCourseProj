using BackEnd.Models;
using BackEnd.Services;

namespace BackEnd.Tests.Services;

public sealed class NotificationSettingsTests
{
    [Theory]
    [InlineData("00:00")]
    [InlineData("23:59")]
    public void ValidateAcceptsBoundaryTimes(string time) =>
        Assert.Equal(new NotificationSettings(true, time), new NotificationSettingsRequest(true, time).Validate());

    [Theory]
    [InlineData("7:00")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("12:00:00")]
    [InlineData(" 12:00")]
    [InlineData(null)]
    public void ValidateRejectsMissingOrNonCanonicalTimes(string? time) =>
        Assert.Equal("validation_error",
            Assert.Throws<HabitApiException>(() => new NotificationSettingsRequest(true, time).Validate()).Code);
}
