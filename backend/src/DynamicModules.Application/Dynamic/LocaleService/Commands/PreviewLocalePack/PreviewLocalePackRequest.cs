using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.PreviewLocalePack;

/// <summary>
/// 預覽語系包（<c>POST i18n/packs/preview</c>；語系包設計 §6.3 第 2 步）：回報告，匯入開放時另存 10 分鐘的預覽暫存（交易型，因為要寫暫存列）。
/// 只寫暫存、不改任何設定，不稽核（套用時才稽核，<c>AuditCoverageTests.Exempt</c>）。
/// </summary>
public sealed record PreviewLocalePackRequest(byte[] Content, string? FileName) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
