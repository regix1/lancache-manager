using System.Text.Json;
using LancacheManager.Infrastructure.Data.Converters;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// The wire spelling and the database spelling of <see cref="SessionType"/> are a contract with rows
/// that are already stored in <c>UserSessions.SessionType</c> (a text column, converted by
/// <see cref="LowercaseStringEnumConverter{TEnum}"/> at AppDbContext.cs:202-205) and with a frontend
/// that compares the raw string (auth.service.ts:80,82). Adding a member must leave both spellings of
/// the existing members exactly where they are, or every stored session changes meaning.
/// </summary>
public class SessionTypeWireContractTests
{
    [Theory]
    [InlineData(SessionType.Admin, "admin")]
    [InlineData(SessionType.User, "user")]
    [InlineData(SessionType.Guest, "guest")]
    public void EveryMemberRoundTripsThroughTheJsonAndDatabaseConverters(SessionType sessionType, string stored)
    {
        Assert.Equal($"\"{stored}\"", JsonSerializer.Serialize(sessionType));
        Assert.Equal(sessionType, JsonSerializer.Deserialize<SessionType>($"\"{stored}\""));

        var databaseConverter = new LowercaseStringEnumConverter<SessionType>();
        Assert.Equal(stored, databaseConverter.ConvertToProvider(sessionType));
        Assert.Equal(sessionType, databaseConverter.ConvertFromProvider(stored));
    }

    [Fact]
    public void SessionTypeCarriesExactlyTheThreeKnownMembers()
    {
        Assert.Equal(
            new[] { SessionType.Admin, SessionType.User, SessionType.Guest },
            Enum.GetValues<SessionType>());
    }
}
