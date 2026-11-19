using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;

/// <summary>
/// 產生 Demo 資料（整批一個交易，任何一步失敗全部回滾）。不是 IDesignerRequest：設計器權限由觸發端把關；
/// 它以管理員身分開套件、寫欄位權限與示範帳號的群組成員，等於批次授權，所以自己宣告權限設定＋系統管理員（權限軸設計 M4、T13）：
/// 背景工作帶不到前台的寫入許可，要自己的 behavior 開。內層指令以 ActingScope 切換身分送出。Progress 每完成一個步驟回報一次（完成數、總數）。
/// </summary>
public record GenerateDemoDataRequest(DateOnly Today, IProgress<(int Done, int Total)>? Progress = null)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IPermissionAdminRequest;

public record DemoGenerateResult(int Steps);

/// <summary>409 的附加資料：每一項衝突一則訊息。</summary>
public record DemoConflicts(List<string> Items);
