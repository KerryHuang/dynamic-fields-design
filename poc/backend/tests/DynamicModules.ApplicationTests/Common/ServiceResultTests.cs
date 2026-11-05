using FluentAssertions;
using DynamicModules.Application.Common;

namespace DynamicModules.ApplicationTests.Common;

public class ServiceResultTests
{
    [Fact]
    public void Success_CarriesData()
    {
        var r = ServiceResult.Success(42);
        r.IsSuccess.Should().BeTrue();
        r.Data.Should().Be(42);
        r.IsNotFound.Should().BeFalse();
        r.IsConflict.Should().BeFalse();
        r.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Created_IsSuccessAndCreated()
    {
        var r = ServiceResult.Created("x");
        r.IsSuccess.Should().BeTrue();
        r.IsCreated.Should().BeTrue();
        r.Data.Should().Be("x");
    }

    [Fact]
    public void NotFound_And_Conflict_AreFailuresWithMessage()
    {
        var nf = ServiceResult.NotFound("nope");
        nf.IsSuccess.Should().BeFalse();
        nf.IsNotFound.Should().BeTrue();
        nf.Message.Should().Be("nope");

        var c = ServiceResult.Conflict("dup");
        c.IsSuccess.Should().BeFalse();
        c.IsConflict.Should().BeTrue();
        c.Message.Should().Be("dup");
    }

    [Fact]
    public void Forbidden_IsFailureWithFixedMessage()
    {
        var r = ServiceResult.Forbidden();
        r.IsForbidden.Should().BeTrue();
        r.IsSuccess.Should().BeFalse();
        r.Message.Should().Be("沒有權限執行此操作");
    }

    [Fact]
    public void Invalid_IsFailureWithErrors()
    {
        var r = ServiceResult.Invalid(new Dictionary<string, string[]> { ["Name"] = ["必填"] });
        r.IsSuccess.Should().BeFalse();
        r.IsNotFound.Should().BeFalse();
        r.IsConflict.Should().BeFalse();
        r.Errors["Name"].Should().Equal("必填");
    }

    [Fact]
    public void PagedRequest_ClampsPagesSizeTo200()
    {
        new PagedRequest().PagesSize.Should().Be(10);
        new PagedRequest().CurrentPage.Should().Be(1);
        new PagedRequest { PagesSize = 999 }.PagesSize.Should().Be(200);
        new PagedRequest { PagesSize = 50 }.PagesSize.Should().Be(50);
    }
}
