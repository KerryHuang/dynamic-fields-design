namespace DynamicModules.Application.Dynamic.AccountService.Data;

/// <summary>登入、改密碼成功的結果；SecurityStamp 只給 controller 簽發 cookie，不回給前端。</summary>
public record AuthUserResponse(string UserId, string DisplayName, bool MustChangePassword, string SecurityStamp);

public record DemoAccountResponse(string UserId, string DisplayName);

/// <summary>登入頁選項；未開啟 Demo 一鍵登入時 DemoAccounts 是空陣列。</summary>
public record AuthOptionsResponse(bool DemoLoginEnabled, bool DemoGenerating, List<DemoAccountResponse> DemoAccounts);
