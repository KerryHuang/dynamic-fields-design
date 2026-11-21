using System.Reflection;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>
/// 工具類別只經既有 request 取資料（mcp1a 計畫判斷 19、T4）：建構函式參數只能是 <c>ISender</c>／<c>IMediator</c>，
/// 欄位不得是 DbContext 或任何 repository。
/// </summary>
public class McpToolArchitectureTests
{
    private static readonly Type[] AllowedCtorParams = [typeof(ISender), typeof(IMediator)];

    private static IEnumerable<string> Violations(IEnumerable<Type> tools)
    {
        foreach (var t in tools)
        {
            foreach (var p in t.GetConstructors().SelectMany(c => c.GetParameters()).Where(p => !AllowedCtorParams.Contains(p.ParameterType)))
                yield return $"{t.Name} 建構函式收 {p.ParameterType.Name}";
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Where(f => Forbidden(f.FieldType)))
                yield return $"{t.Name} 欄位 {f.Name} 是 {f.FieldType.Name}";
        }
    }

    private static bool Forbidden(Type t) => typeof(DbContext).IsAssignableFrom(t) || t.Name.EndsWith("Repository", StringComparison.Ordinal);

    private static IEnumerable<Type> ToolsIn(Assembly assembly) =>
        assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMcpTool).IsAssignableFrom(t));

    [Fact]
    public void ApplicationTools_OnlyTakeSender()
    {
        var tools = ToolsIn(typeof(IMcpTool).Assembly).ToList();
        tools.Should().NotBeEmpty();
        Violations(tools).Should().BeEmpty();
    }

    [Fact]
    public void Rule_CatchesRepositoryTool()
    {
        Violations([typeof(RepositoryTool)]).Should().HaveCount(2, "建構函式參數與欄位各一條");
    }

    private sealed class RepositoryTool(IRecordRepository records) : IMcpTool
    {
        private readonly IRecordRepository _records = records;
        public McpToolDescriptor Descriptor => throw new NotSupportedException();
        public string InputSchemaJson => "{}";
        public string Description => "bad";
        public Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct) { _ = _records; throw new NotSupportedException(); }
    }
}
