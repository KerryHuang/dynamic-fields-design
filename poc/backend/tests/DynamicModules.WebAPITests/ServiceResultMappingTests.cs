using System.Text.Json;
using DynamicModules.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using DynamicModules.WebAPI.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPITests;

public class ServiceResultMappingTests
{
    private class TestController : ControllerBase
    {
        public TestController(string? locale = null)
        {
            var http = new DefaultHttpContext { TraceIdentifier = "trace-1" };
            if (locale is not null)
                http.RequestServices = new ServiceCollection().AddSingleton<ICurrentLocale>(new FixedLocale(locale)).BuildServiceProvider();
            ControllerContext = new ControllerContext { HttpContext = http };
        }

        [HttpGet("{id}")]
        public IActionResult Get(int id) => Ok();
    }

    private sealed record FixedLocale(string Locale) : ICurrentLocale;

    private static readonly TestController C = new();
    private static readonly TestController En = new("en-US");

    private static JsonElement BodyOf(IActionResult r) =>
        JsonDocument.Parse(JsonSerializer.Serialize(((ObjectResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

    [Fact]
    public void Forbidden_InEnUs_IsEnglish()
    {
        var res = ServiceResult.Forbidden().ToActionResult(En);
        BodyOf(res).GetProperty("message").GetString().Should().Be("You do not have permission to perform this action");
    }

    [Fact]
    public void Stale_CarriesCodeAndLocalizedMessage()
    {
        var en = BodyOf(ServiceResult.Stale().ToActionResult(En));
        en.GetProperty("code").GetString().Should().Be("stale");
        en.GetProperty("message").GetString().Should().Be("This data was changed by someone else. Please reload");
        var zh = ServiceResult.Stale().ToActionResult(C).Should().BeOfType<ConflictObjectResult>().Subject;
        BodyOf(zh).GetProperty("message").GetString().Should().Be("資料已被其他人修改，請重新載入");
        ServiceResult.Stale().Message.Should().Be("資料已被其他人修改，請重新載入");
    }

    [Fact]
    public void Invalid_KeyedErrors_ResolvedPerLocale()
    {
        var r = ServiceResult.Invalid("oldPassword", MessageRef.Of(MessageText.OldPasswordWrong));
        BodyOf(r.ToActionResult(En)).GetProperty("errors").GetProperty("oldPassword")[0].GetString().Should().Be("The current password is incorrect");
        r.Errors["oldPassword"].Should().Equal("舊密碼錯誤");
    }

    [Fact]
    public void Literal_ThatMatchesTable_IsTranslated_OtherLiteralsAsIs()
    {
        BodyOf(ServiceResult.NotFound("找不到模組").ToActionResult(En)).GetProperty("message").GetString().Should().Be("Module not found");
        BodyOf(ServiceResult.NotFound("找不到欄位").ToActionResult(En)).GetProperty("message").GetString().Should().Be("找不到欄位");
    }

    [Fact]
    public void Mapping_Invalid_Returns400WithTraceIdAndErrors()
    {
        var r = ServiceResult.Invalid(new Dictionary<string, string[]> { ["f_12"] = ["必填"] });

        var res = r.ToActionResult(C).Should().BeOfType<BadRequestObjectResult>().Subject;

        res.StatusCode.Should().Be(400);
        var json = JsonSerializer.Serialize(res.Value);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("traceId").GetString().Should().Be("trace-1");
        doc.RootElement.GetProperty("errors").GetProperty("f_12")[0].GetString().Should().Be("必填");
    }

    [Fact]
    public void Mapping_NotFound_Returns404()
        => ServiceResult.NotFound("x").ToActionResult(C).Should().BeOfType<NotFoundObjectResult>()
            .Which.StatusCode.Should().Be(404);

    [Fact]
    public void Mapping_Conflict_Returns409()
        => ServiceResult.Conflict("x").ToActionResult(C).Should().BeOfType<ConflictObjectResult>()
            .Which.StatusCode.Should().Be(409);

    [Fact]
    public void ConflictWith_MapsTo409WithData()
    {
        var res = ServiceResult.ConflictWith("x", new { References = new[] { new { RecordId = 7L } }, Total = 12 })
            .ToActionResult(C).Should().BeOfType<ConflictObjectResult>().Subject;
        res.StatusCode.Should().Be(409);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(res.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        doc.RootElement.GetProperty("message").GetString().Should().Be("x");
        doc.RootElement.GetProperty("references")[0].GetProperty("recordId").GetInt64().Should().Be(7);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(12);
    }

    [Fact]
    public void Conflict_WithoutData_BodyOnlyMessage()
    {
        var res = ServiceResult.Conflict("x").ToActionResult(C).Should().BeOfType<ConflictObjectResult>().Subject;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(res.Value));
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("message");
    }

    [Fact]
    public void Forbidden_MapsTo403()
    {
        var res = ServiceResult.Forbidden().ToActionResult(C).Should().BeOfType<ObjectResult>().Subject;
        res.StatusCode.Should().Be(403);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(res.Value));
        doc.RootElement.GetProperty("message").GetString().Should().Be("沒有權限執行此操作");
    }

    [Fact]
    public void Mapping_Success_Returns200WithData()
    {
        var res = ServiceResult.Success(5).ToActionResult(C).Should().BeOfType<OkObjectResult>().Subject;
        res.Value.Should().Be(5);
    }

    [Fact]
    public void Mapping_Created_Returns201()
    {
        var res = ServiceResult.Created(new { id = 1 })
            .ToActionResult(C, nameof(TestController.Get), new { id = 1 })
            .Should().BeOfType<CreatedAtActionResult>().Subject;
        res.StatusCode.Should().Be(201);
    }

    [Fact]
    public void Mapping_CreatedWithoutAction_Returns201()
        => ServiceResult.Created(1).ToActionResult(C).Should().BeAssignableTo<ObjectResult>()
            .Which.StatusCode.Should().Be(201);
}
