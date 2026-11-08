using DynamicModules.Application.Dynamic.AppService;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>內建套件的目錄層靜態檢查：跨套件事件循環、夥伴欄位對帳（虛構的兩個套件 ping、pong）。</summary>
public class AppCatalogChecksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("catalog-checks-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Integration(string id, string module, string partner, string partnerModule, string partnerField) => $$"""
        [ { "code": "link", "name": { "zh-TW": "連動" }, "with": [ { "id": "{{partner}}", "modules": [ "{{partnerModule}}" ] } ], "onPartnerClosed": "skip",
            "events": [ { "code": "{{id}}-copy", "name": { "zh-TW": "複製" }, "active": true, "source": "{{module}}",
              "trigger": { "on": "create" }, "when": null,
              "effects": [ { "code": "copy", "kind": "create", "target": "{{partner}}/{{partnerModule}}",
                "set": [ { "to": "{{partner}}/{{partnerModule}}.{{partnerField}}", "from": { "side": "header", "field": "{{module}}.name" } } ] } ] } ] } ]
        """;

    private static string Package(string id, string module, string integrations = "[]") => $$"""
        {
          "format": "formwright-app", "formatVersion": 5,
          "manifest": { "id": "{{id}}", "version": "1.0.0", "name": { "zh-TW": "{{id}}" } },
          "modules": [ { "code": "{{module}}", "key": "{{id}}-{{module}}", "name": { "zh-TW": "{{module}}" }, "kind": "standalone", "titleField": "name",
            "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
              { "code": "name", "type": "text", "label": { "zh-TW": "name" }, "config": { "required": true } } ] } ] } ],
          "integrations": {{integrations}}
        }
        """;

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_dir, $"{name}.app.json"), json);

    private string LoadError()
    {
        var act = () => AppCatalog.Load(_dir);
        return act.Should().Throw<InvalidOperationException>().Which.Message;
    }

    [Fact]
    public void TwoIntegrations_ThatTogetherFormACycle_RefuseStartup()
    {
        Write("ping", Package("ping", "alpha", Integration("ping", "alpha", "pong", "bravo", "name")));
        Write("pong", Package("pong", "bravo", Integration("pong", "bravo", "ping", "alpha", "name")));

        LoadError().Should().Contain("連動事件形成循環").And.Contain("ping:ping-copy").And.Contain("pong:pong-copy");
    }

    [Fact]
    public void TwoIntegrations_WithoutACycle_Load()
    {
        Write("ping", Package("ping", "alpha", Integration("ping", "alpha", "pong", "bravo", "name")));
        Write("pong", Package("pong", "bravo"));

        AppCatalog.Load(_dir).BuiltIns.Should().HaveCount(2);
    }

    [Fact]
    public void APartnerFieldThatDoesNotExist_RefusesStartup()
    {
        Write("ping", Package("ping", "alpha", Integration("ping", "alpha", "pong", "bravo", "typo")));
        Write("pong", Package("pong", "bravo"));

        LoadError().Should().Contain("pong 沒有的欄位").And.Contain("pong/bravo.typo");
    }

    [Fact]
    public void APartnerThatIsNotInTheCatalog_IsLeftForInstallTime()
    {
        Write("ping", Package("ping", "alpha", Integration("ping", "alpha", "pong", "bravo", "typo")));

        AppCatalog.Load(_dir).BuiltIns.Should().HaveCount(1);
    }
}
