using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

public class AdminUsersApiTests
{
    private const string Base = "/api/v3/dynamic";
    private const string Admin = Base + "/admin/users";

    private static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static T Db<T>(TestApp app, Func<DynamicDbContext, T> read)
    {
        using var scope = app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private static void Write(TestApp app, Action<DynamicDbContext> change)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        change(db);
        db.SaveChanges();
    }

    private static long GroupId(TestApp app, string key) => Db(app, db => db.Set<PermissionGroup>().Single(g => g.Key == key).Id);

    private static object NewUser(string id, string password = "temp-pass-1", long[]? groups = null, bool admin = false) =>
        new { userId = id, displayName = id, password, groupIds = groups ?? Array.Empty<long>(), isSystemAdmin = admin };

    private static async Task<HttpClient> RealLogin(TestApp app, string user, string password)
    {
        var client = app.CreateClient();
        (await client.PostAsJsonAsync($"{Base}/auth/login", new { userId = user, password })).StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private static TestApp RealApp()
    {
        var app = new TestApp(realAuth: true);
        Write(app, db => db.Set<AppUser>().Single(u => u.UserId == "admin").MustChangePassword = false);
        return app;
    }

    [Fact]
    public async Task AdminEndpoints_NonAdmin_403()
    {
        using var app = new TestApp();
        var c = app.AsUser("sales01");

        (await c.GetAsync(Admin)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PostAsJsonAsync(Admin, NewUser("zed"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PutAsJsonAsync($"{Admin}/sales02", new { displayName = "x", groupIds = Array.Empty<long>(), isSystemAdmin = false }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PostAsJsonAsync($"{Admin}/sales02/reset-password", new { password = "temp-pass-1" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PostAsync($"{Admin}/sales02/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PostAsync($"{Admin}/sales02/activate", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateUser_NormalizesToLowercase_MustChangePassword()
    {
        using var app = new TestApp();

        var res = await app.AsUser().PostAsJsonAsync(Admin, NewUser("Ann.Lee"));

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var u = Db(app, db => db.Set<AppUser>().Single(x => x.UserId == "ann.lee"));
        u.MustChangePassword.Should().BeTrue();
        u.PasswordHash.Should().NotBeNullOrEmpty().And.NotBe("temp-pass-1");
        u.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateUser_Duplicate_409()
    {
        using var app = new TestApp();

        (await app.AsUser().PostAsJsonAsync(Admin, NewUser("SALES01"))).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a b")]
    [InlineData("王小明")]
    public async Task CreateUser_InvalidId_400(string id)
    {
        using var app = new TestApp();

        (await app.AsUser().PostAsJsonAsync(Admin, NewUser(id))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateUser_ShortPassword_400()
    {
        using var app = new TestApp();

        (await app.AsUser().PostAsJsonAsync(Admin, NewUser("zed", "short"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateUser_UnknownGroup_400()
    {
        using var app = new TestApp();

        (await app.AsUser().PostAsJsonAsync(Admin, NewUser("zed", groups: [99999]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Db(app, db => db.Set<AppUser>().Any(x => x.UserId == "zed")).Should().BeFalse();
    }

    [Fact]
    public async Task CreateUser_MultipleGroups_AppearInMeAndList()
    {
        using var app = new TestApp();
        long[] groups = [GroupId(app, "sales"), GroupId(app, "qa")];

        (await app.AsUser().PostAsJsonAsync(Admin, NewUser("zed", groups: groups))).StatusCode.Should().Be(HttpStatusCode.Created);

        // TestAuthHandler 對任何存在的帳號都簽發當下 stamp，不需先改密碼
        var me = await Json(await app.AsUser("zed").GetAsync($"{Base}/me"));
        me.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("id").GetInt64()).Should().BeEquivalentTo(groups);
        var row = (await Json(await app.AsUser().GetAsync(Admin))).EnumerateArray().Single(u => u.GetProperty("userId").GetString() == "zed");
        row.GetProperty("groupIds").EnumerateArray().Select(g => g.GetInt64()).Should().BeEquivalentTo(groups);
        row.GetProperty("isActive").GetBoolean().Should().BeTrue();
        row.GetProperty("isLocked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CreateUser_ThenLoginWithRealAuth()
    {
        using var app = RealApp();
        var admin = await RealLogin(app, "admin", "initial-pass-1");

        (await admin.PostAsJsonAsync(Admin, NewUser("zed", "temp-pass-1"))).StatusCode.Should().Be(HttpStatusCode.Created);

        var zed = await RealLogin(app, "zed", "temp-pass-1");
        (await Json(await zed.GetAsync($"{Base}/me"))).GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task UpdateUser_UserIdUnchanged_GroupsReplaced()
    {
        using var app = new TestApp();
        var qa = GroupId(app, "qa");

        var res = await app.AsUser().PutAsJsonAsync($"{Admin}/sales01", new { displayName = "改名", groupIds = new[] { qa }, isSystemAdmin = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        Db(app, db => db.Set<AppUser>().Single(u => u.UserId == "sales01").DisplayName).Should().Be("改名");
        Db(app, db => db.Set<UserGroupMapping>().Where(m => m.UserId == "sales01").Select(m => m.GroupId).ToList()).Should().Equal(qa);
    }

    [Fact]
    public async Task UpdateUser_Unknown_404()
    {
        using var app = new TestApp();

        (await app.AsUser().PutAsJsonAsync($"{Admin}/nobody", new { displayName = "x", groupIds = Array.Empty<long>(), isSystemAdmin = false }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ResetPassword_UnlocksAndRequiresChange_InvalidatesSessions()
    {
        using var app = RealApp();
        Write(app, db => db.Set<AppUser>().Add(new AppUser { UserId = "zed", DisplayName = "zed", MustChangePassword = false }));
        var admin = await RealLogin(app, "admin", "initial-pass-1");
        (await admin.PostAsJsonAsync($"{Admin}/zed/reset-password", new { password = "first-pass-1" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var old = await RealLogin(app, "zed", "first-pass-1");
        Write(app, db =>
        {
            var u = db.Set<AppUser>().Single(x => x.UserId == "zed");
            u.FailedLoginCount = 5;
            u.LockedUntil = app.Time.GetUtcNow().AddMinutes(10);
        });

        (await admin.PostAsJsonAsync($"{Admin}/zed/reset-password", new { password = "second-pass-1" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await old.GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var fresh = await RealLogin(app, "zed", "second-pass-1");
        (await Json(await fresh.GetAsync($"{Base}/me"))).GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ResetPassword_ShortPassword_400()
    {
        using var app = new TestApp();

        (await app.AsUser().PostAsJsonAsync($"{Admin}/sales01/reset-password", new { password = "short" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deactivate_InvalidatesSessionImmediately()
    {
        using var app = RealApp();
        var admin = await RealLogin(app, "admin", "initial-pass-1");
        (await admin.PostAsJsonAsync(Admin, NewUser("zed", "temp-pass-1"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var zed = await RealLogin(app, "zed", "temp-pass-1");
        (await zed.GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PostAsync($"{Admin}/zed/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await zed.GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await admin.PostAsync($"{Admin}/zed/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        Db(app, db => db.Set<AppUser>().Single(u => u.UserId == "zed").IsActive).Should().BeTrue();
    }

    [Fact]
    public async Task Deactivate_Self_400()
    {
        using var app = new TestApp();
        Write(app, db => db.Set<AppUser>().Add(new AppUser { UserId = "boss2", DisplayName = "boss2", IsSystemAdmin = true }));

        (await app.AsUser("admin").PostAsync($"{Admin}/admin/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Db(app, db => db.Set<AppUser>().Single(u => u.UserId == "admin").IsActive).Should().BeTrue();
    }

    [Fact]
    public async Task Deactivate_LastAdmin_400()
    {
        using var app = new TestApp();

        var res = await app.AsUser().PostAsync($"{Admin}/admin/deactivate", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(res)).GetProperty("errors").GetProperty("userId")[0].GetString().Should().Contain("最後一位");
        Db(app, db => db.Set<AppUser>().Single(u => u.UserId == "admin").IsActive).Should().BeTrue();
    }

    [Fact]
    public async Task Demote_LastAdmin_400()
    {
        using var app = new TestApp();

        var res = await app.AsUser().PutAsJsonAsync($"{Admin}/admin", new { displayName = "管理員", groupIds = Array.Empty<long>(), isSystemAdmin = false });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Db(app, db => db.Set<AppUser>().Single(u => u.UserId == "admin").IsSystemAdmin).Should().BeTrue();
    }

    [Fact]
    public async Task Demote_Self_WhenOtherAdminExists_Allowed()
    {
        using var app = new TestApp();
        Write(app, db => db.Set<AppUser>().Add(new AppUser { UserId = "boss2", DisplayName = "boss2", IsSystemAdmin = true }));

        var res = await app.AsUser().PutAsJsonAsync($"{Admin}/admin", new { displayName = "管理員", groupIds = Array.Empty<long>(), isSystemAdmin = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.AsUser().GetAsync(Admin)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Users_ExcludesInactive()
    {
        using var app = new TestApp();
        (await app.AsUser().PostAsync($"{Admin}/sales02/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var plain = (await Json(await app.AsUser().GetAsync($"{Base}/users"))).EnumerateArray().Select(u => u.GetProperty("userId").GetString()).ToList();
        var admin = (await Json(await app.AsUser().GetAsync(Admin))).EnumerateArray()
            .Where(u => !u.GetProperty("isActive").GetBoolean()).Select(u => u.GetProperty("userId").GetString()).ToList();

        plain.Should().NotContain("sales02").And.Contain("sales01");
        admin.Should().Equal("sales02");
    }
}
