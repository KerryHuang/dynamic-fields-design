using DynamicModules.Application.Common.Security;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>測試用的可切換呼叫通道（WebAPI 的正式實作讀 claim，見 McpPermissionDiTests）。</summary>
public sealed class TestRequestChannel : IRequestChannel
{
    public RequestChannel Channel { get; set; } = RequestChannel.Ui;
    public long? TokenId => Channel == RequestChannel.Mcp ? 1 : null;
    public IReadOnlyList<string> Scopes => [];
}
