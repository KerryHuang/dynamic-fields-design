using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;

/// <summary>
/// 產生 Demo 資料（整批一個交易，任何一步失敗全部回滾）。不是 IDesignerRequest：權限由觸發端把關；
/// 內層指令以 ActingScope 切換身分送出。Progress 每完成一個步驟回報一次（完成數、總數）。
/// </summary>
public record GenerateDemoDataRequest(DateOnly Today, IProgress<(int Done, int Total)>? Progress = null)
    : IRequest<ServiceResult>, ITransactionalRequest;

public record DemoGenerateResult(int Steps);

/// <summary>409 的附加資料：每一項衝突一則訊息。</summary>
public record DemoConflicts(List<string> Items);
