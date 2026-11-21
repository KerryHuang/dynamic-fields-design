using System.Reflection;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.McpService.Confirmations;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Application.Dynamic.McpService.Writes;
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

    /// <summary>寫入工具送的請求：不是稽核或確認請求（一次呼叫只能一列，mcp2a 判斷 3），而且都是 IIdempotentRequest。</summary>
    [Fact]
    public void WriteTargets_NotAuditedOrConfirmed_AllIdempotent()
    {
        McpWriteTargets.Covered.Should().HaveCount(9);
        McpWriteTargets.Covered.Where(t => typeof(IAuditedRequest).IsAssignableFrom(t) || typeof(IMcpConfirmedRequest).IsAssignableFrom(t))
            .Should().BeEmpty();
        // 上膛期間只有 IIdempotentRequest 會被 McpWriteAuditBehavior 接手，其他交易型請求放行（複審建議 2）：
        // 寫入工具送的請求一定要是 IIdempotentRequest，否則稽核會變成交易外的事後寫入
        McpWriteTargets.Covered.Where(t => !typeof(IIdempotentRequest).IsAssignableFrom(t)).Should().BeEmpty();
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
