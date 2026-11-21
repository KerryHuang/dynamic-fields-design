using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.WriteLoop;

/// <summary>AI 寫入迴圈事件的代碼（MCP 設計 §20 C3、mcp2a 計畫判斷 8）。</summary>
public static class McpWriteLoopIncident
{
    public const string Code = "mcp.writeLoop";

    /// <summary>指紋＝代碼＋權杖 id＋記錄 id：同一把權杖對同一筆反覆被擋只累加次數。</summary>
    public static string Fingerprint(long tokenId, long recordId) =>
        ErrorFingerprint.ForInfra(Code, $"{tokenId}:{recordId}");
}

/// <summary>
/// 開或更新「AI 寫入迴圈」事件（判斷 8）：執行器在交易外，<see cref="IncidentRecorder"/> 只能在交易型請求裡用，所以另開 scope 送這個請求。
/// 不是 <c>IIdempotentRequest</c>，不會被寫入稽核當成本次呼叫的寫入。<paramref name="Count"/> 是時間窗內已成功的寫入次數。
/// </summary>
public sealed record RecordMcpWriteLoopIncidentRequest(long TokenId, long RecordId, int Count, int WindowMinutes, DateTime OccurredUtc)
    : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>
/// 安全類、嚴重度警告：處理者是系統管理員（撤銷權杖），收件人照 <c>ops.alertRecipients</c>，出廠的告警信類別含安全類所以會寄信。
/// 來源維持 <see cref="IncidentSource.Config"/>：通知只在新開、回歸時發，同指紋再發生只累加次數；來源若改成安全類，鋸齒期間每擋一次就通知一次
/// （第 4 批審查 S1，主控裁決，待使用者確認）。標題只放代碼與數字（O2-T22）：權杖名稱與擁有者是使用者輸入的字串，不進事件。
/// </summary>
public sealed class RecordMcpWriteLoopIncidentHandler(IncidentRecorder recorder, IOpsEnvironment env)
    : IRequestHandler<RecordMcpWriteLoopIncidentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RecordMcpWriteLoopIncidentRequest r, CancellationToken ct)
    {
        await recorder.RecordAsync(new IncidentSignal
        {
            Fingerprint = McpWriteLoopIncident.Fingerprint(r.TokenId, r.RecordId), Source = IncidentSource.Config,
            Category = IncidentCategory.Security, Severity = IncidentSeverity.Warning, Code = McpWriteLoopIncident.Code,
            TitleKey = IncidentTitles.McpWriteLoop, TitleParams = IncidentTitles.WriteLoop(r.TokenId, r.RecordId, r.Count, r.WindowMinutes),
            OccurredUtc = r.OccurredUtc, Version = env.Version,
        }, ct);
        return ServiceResult.Success();
    }
}
