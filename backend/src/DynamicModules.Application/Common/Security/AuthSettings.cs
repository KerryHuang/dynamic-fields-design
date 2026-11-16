namespace DynamicModules.Application.Common.Security;

/// <summary>驗證相關的站台設定（singleton）。DemoLoginEnabled 來自 Demo:LoginEnabled，只該在公開的 Demo 站開啟。</summary>
public sealed record AuthSettings(bool DemoLoginEnabled);
