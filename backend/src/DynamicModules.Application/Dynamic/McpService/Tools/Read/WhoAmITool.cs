using DynamicModules.Application.Common;
using DynamicModules.Domain.Mcp;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tools.Read;

/// <summary><c>whoami</c>：不需範圍，回權杖擁有者、權杖、上限與伺服器版本（設計 U5）。</summary>
public sealed class WhoAmITool(ISender sender) : IMcpTool
{
    public McpToolDescriptor Descriptor { get; } = McpToolCatalog.Find("whoami")!;
    public string InputSchemaJson => McpTexts.NoArgumentsSchema;
    public string Description => McpTexts.WhoAmIDescription;

    public async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var r = await sender.Send(new GetMcpWhoAmIRequest(), ct);
        return r.IsSuccess ? McpToolOutcome.Ok(r.Data) with { TargetKind = "user", TargetId = ((McpWhoAmIResponse)r.Data!).UserId }
            : McpToolOutcome.From(r);
    }
}
