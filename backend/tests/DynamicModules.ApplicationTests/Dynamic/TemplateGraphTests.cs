using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>第 8 階段：範本內的依賴、鎖定欄位、計算順序（用內建訂單範本）；以及內建範本的載入。</summary>
public class TemplateGraphTests
{
    private static TemplatePackage Order()
    {
        var (package, error) = TemplateJson.Parse(File.ReadAllText(OrderTemplate.Path));
        error.Should().BeNull();
        return package!;
    }

    [Fact]
    public void Order_ComputedOrder_AmountBeforeSubtotalBeforeTaxBeforeTotal()
    {
        var graph = TemplateGraph.Build(Order());
        graph.HasCycle.Should().BeFalse();
        var order = graph.ComputedOrder().ToList();
        order.Should().Contain(["line.amount", "order.subtotal", "order.tax", "order.total", "order.lineCount"]);
        order.IndexOf("line.amount").Should().BeLessThan(order.IndexOf("order.subtotal"));
        order.IndexOf("order.subtotal").Should().BeLessThan(order.IndexOf("order.tax"));
        order.IndexOf("order.tax").Should().BeLessThan(order.IndexOf("order.total"));
    }

    [Fact]
    public void Order_Locked()
    {
        var graph = TemplateGraph.Build(Order());
        graph.IsLocked("order.no").Should().BeTrue();
        graph.IsLocked("line.order").Should().BeTrue();
        graph.IsLocked("line.itemName").Should().BeTrue();
        graph.IsLocked("order.remark").Should().BeFalse();
    }

    [Fact]
    public void Order_DependsOn()
    {
        var graph = TemplateGraph.Build(Order());
        graph.DependsOn("order.tax").Should().Equal("order.subtotal", "order.taxRate");
        graph.DependsOn("order.subtotal").Should().Equal("line.order", "line.amount");
        graph.DependsOn("line.dueDate").Should().Equal("order.dueDate");
        graph.DependsOn("order.remark").Should().BeEmpty();
    }

    [Fact]
    public void Load_BuiltInDirectory_HasOrder()
    {
        var catalog = TemplateCatalog.Load(System.IO.Path.GetDirectoryName(OrderTemplate.Path)!);
        catalog.BuiltIns.Should().ContainSingle(t => t.Key == "order");
    }

    [Fact]
    public void Load_MissingDirectory_ReturnsEmptyCatalog()
    {
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "templates-missing-" + Guid.NewGuid().ToString("N"));
        TemplateCatalog.Load(missing).BuiltIns.Should().BeEmpty();
    }

    [Fact]
    public void Load_InvalidFile_ThrowsWithFileName()
    {
        var dir = Directory.CreateTempSubdirectory("templates-").FullName;
        try
        {
            File.Copy(OrderTemplate.Path, System.IO.Path.Combine(dir, "order.json"));
            File.WriteAllText(System.IO.Path.Combine(dir, "broken.json"),
                File.ReadAllText(OrderTemplate.Path).Replace("{subtotal} + {tax}", "{subtotal} + {vat}"));

            var act = () => TemplateCatalog.Load(dir);

            act.Should().Throw<InvalidOperationException>()
                .Which.Message.Should().Contain("broken.json").And.Contain("order.total: 公式引用了不存在的欄位 vat")
                .And.Contain("order.json: 範本代號 order 與 broken.json 重複");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_DuplicateKey_Throws()
    {
        var dir = Directory.CreateTempSubdirectory("templates-").FullName;
        try
        {
            File.Copy(OrderTemplate.Path, System.IO.Path.Combine(dir, "a.json"));
            File.Copy(OrderTemplate.Path, System.IO.Path.Combine(dir, "b.json"));
            var act = () => TemplateCatalog.Load(dir);
            act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("b.json: 範本代號 order 與 a.json 重複");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Ctor_FromPackages_ForTests()
    {
        var catalog = new TemplateCatalog([Order()]);
        catalog.BuiltIns.Should().ContainSingle().Which.Key.Should().Be("order");
    }
}
