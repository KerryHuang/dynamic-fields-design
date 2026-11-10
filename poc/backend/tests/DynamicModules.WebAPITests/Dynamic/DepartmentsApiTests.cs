using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class DepartmentsApiTests
{
    private const string Departments = Base + "/departments";

    private static object Body(string code, long? parentId = null, string? managerUserId = null, bool active = true) => new
    {
        code, name = new Dictionary<string, string> { ["zh-TW"] = code }, parentId, managerUserId, sortOrder = 0, active
    };

    [Fact]
    public async Task DepartmentsApi_CrudAndUserDepartment()
    {
        using var app = new TestApp();
        var c = app.AsUser();

        var created = await c.PostAsJsonAsync(Departments, Body("hq", managerUserId: "manager01"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var hq = (await JsonOf(created)).GetProperty("id").GetInt64();
        var sales = (await JsonOf(await c.PostAsJsonAsync(Departments, Body("sales", hq)))).GetProperty("id").GetInt64();

        (await c.PutAsJsonAsync($"{Departments}/{sales}", Body("sales", hq, "manager01"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var bad = await c.PutAsJsonAsync($"{Departments}/{hq}", Body("hq", sales));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(bad)).GetProperty("errors").GetProperty("parentId")[0].GetString().Should().Be("上級部門不能是自己或自己的下級部門");

        (await c.PutAsJsonAsync($"{Base}/admin/users/sales01/department", new { departmentId = sales }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var users = await JsonOf(await c.GetAsync($"{Base}/admin/users"));
        users.EnumerateArray().Single(u => u.GetProperty("userId").GetString() == "sales01")
            .GetProperty("departmentId").GetInt64().Should().Be(sales);

        // 一般使用者可以讀部門清單（選單用），不能改
        var reader = app.AsUser("sales02");
        var list = await JsonOf(await reader.GetAsync(Departments));
        var row = list.EnumerateArray().Single(d => d.GetProperty("id").GetInt64() == sales);
        row.GetProperty("parentId").GetInt64().Should().Be(hq);
        row.GetProperty("managerUserId").GetString().Should().Be("manager01");
        row.GetProperty("memberUserIds")[0].GetString().Should().Be("sales01");
        (await reader.PostAsJsonAsync(Departments, Body("x"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.PutAsJsonAsync($"{Base}/admin/users/sales01/department", new { departmentId = (long?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var blocked = await c.DeleteAsync($"{Departments}/{sales}");
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(blocked)).GetProperty("message").GetString().Should().Be("部門還有 0 個子部門、1 位成員，請先搬移");

        (await c.PutAsJsonAsync($"{Base}/admin/users/sales01/department", new { departmentId = (long?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        users = await JsonOf(await c.GetAsync($"{Base}/admin/users"));
        // 回應不輸出 null 屬性：沒有部門就沒有 departmentId
        users.EnumerateArray().Single(u => u.GetProperty("userId").GetString() == "sales01")
            .TryGetProperty("departmentId", out _).Should().BeFalse();
        (await c.DeleteAsync($"{Departments}/{sales}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.DeleteAsync($"{Departments}/{sales}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
