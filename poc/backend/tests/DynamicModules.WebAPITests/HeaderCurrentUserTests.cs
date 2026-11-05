using DynamicModules.Application.Common.Security;
using DynamicModules.WebAPI.Security;
using DynamicModules.WebAPI.Time;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace DynamicModules.WebAPITests;

public class HeaderCurrentUserTests
{
    private class Known : IKnownUsers
    {
        public bool Exists(string userId) => userId == "admin";
    }

    private static string Resolve(string? header)
    {
        var ctx = new DefaultHttpContext();
        if (header != null) ctx.Request.Headers["X-User"] = header;
        return new HeaderCurrentUser(new HttpContextAccessor { HttpContext = ctx }, new Known()).UserId;
    }

    [Fact] public void KnownUser_IsReturned() => Resolve("admin").Should().Be("admin");
    [Fact] public void UnknownUser_IsEmpty() => Resolve("hacker").Should().BeEmpty();
    [Fact] public void MissingHeader_IsEmpty() => Resolve(null).Should().BeEmpty();

    [Fact]
    public void TaipeiBusinessTime_IsUtcPlus8()
    {
        var t = new TaipeiBusinessTime();
        (t.Now - DateTime.UtcNow).TotalHours.Should().BeApproximately(8, 0.01);
        t.Today.Should().Be(DateOnly.FromDateTime(t.Now));
    }
}
