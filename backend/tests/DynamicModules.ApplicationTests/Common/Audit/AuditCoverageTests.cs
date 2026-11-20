using System.Runtime.CompilerServices;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Common.Audit;

/// <summary>
/// A05（維運設計 §17.8）：所有交易型、而且是系統管理或設計器寫入的請求都要有稽核宣告；例外列在白名單並寫理由。
/// 宣告的動作代碼要登記在 <see cref="AuditActions"/>，目標種類要是 <see cref="AuditTargets"/> 之一。
/// </summary>
public class AuditCoverageTests
{
    /// <summary>不寫稽核的交易型管理或設計請求（型別 → 理由）。新請求忘了宣告就在這裡失敗。</summary>
    private static readonly Dictionary<Type, string> Exempt = new()
    {
        // 權限軸設計 §7.3、T13：背景產生器另宣告系統管理員只為了讓寫入許可帶 SystemAdminVerified（示範帳號的成員寫入），不是管理請求；內部步驟照常各記一列
        [typeof(DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData.GenerateDemoDataRequest)] =
            "Demo 資料產生器：內部的開套件、建模組等步驟各自寫稽核，整批不另記一列",
        [typeof(DynamicModules.Application.Dynamic.LocaleService.Commands.PreviewLocalePack.PreviewLocalePackRequest)] =
            "只寫 10 分鐘的預覽暫存，不改任何設定；套用時才稽核（語系包 i4d1 計畫判斷 17）",
    };

    /// <summary>可建立的具體型別（含 struct record）。</summary>
    private static IEnumerable<Type> Concrete() =>
        typeof(ITransactionalRequest).Assembly.GetTypes().Where(t => t is { IsInterface: false, IsAbstract: false });

    private static IEnumerable<Type> RequestTypes() =>
        Concrete().Where(t => typeof(ITransactionalRequest).IsAssignableFrom(t))
            .Where(t => typeof(ISystemAdminRequest).IsAssignableFrom(t) || IsDesignerWrite(t));

    private static bool IsDesignerWrite(Type t) =>
        typeof(IDesignerRequest).IsAssignableFrom(t)
        && ((IDesignerRequest)RuntimeHelpers.GetUninitializedObject(t)).RequiredDesignerAction != PermissionAction.Read;

    [Fact]
    public void EveryAdminOrDesignerWrite_IsAudited_OrExemptWithReason()
    {
        var missing = RequestTypes()
            .Where(t => !typeof(IAuditedRequest).IsAssignableFrom(t) && !Exempt.ContainsKey(t))
            .Select(t => t.FullName).OrderBy(n => n).ToList();
        missing.Should().BeEmpty("每個管理與設計類寫入都要實作 IAuditedRequest，或列進白名單並寫理由；缺：" + string.Join("、", missing));
        Exempt.Values.Where(string.IsNullOrWhiteSpace).Should().BeEmpty("白名單每一項都要寫理由");
        Exempt.Keys.Where(t => !RequestTypes().Contains(t)).Should().BeEmpty("白名單只放還存在、而且在檢查範圍內的請求");
    }

    [Fact]
    public void DeclaredActions_AreRegistered_AndTargetKindsAreKnown()
    {
        var targets = typeof(AuditTargets).GetFields().Select(f => (string)f.GetValue(null)!).ToHashSet();
        var audited = Concrete().Where(t => typeof(IAuditedRequest).IsAssignableFrom(t)).ToList();
        audited.Should().NotBeEmpty();
        foreach (var t in audited)
        {
            var action = ((IAuditedRequest)RuntimeHelpers.GetUninitializedObject(t)).Audit;
            AuditActions.Find(action.Code).Should().NotBeNull($"{t.Name} 的動作 {action.Code} 要登記在 AuditActions");
            if (action.TargetKind is not null) targets.Should().Contain(action.TargetKind, t.Name);
        }
    }

    /// <summary>
    /// 反向：宣告稽核的請求一定要經過 AuditBehavior 會寫的路徑——交易型（同交易寫入）、回 <see cref="ServiceResult"/>
    /// （behavior 只在成功時寫）。少了任一個，宣告了也不會寫。
    /// </summary>
    [Fact]
    public void EveryAuditedRequest_IsTransactional_AndReturnsServiceResult()
    {
        var wrong = Concrete().Where(t => typeof(IAuditedRequest).IsAssignableFrom(t))
            .Where(t => !typeof(ITransactionalRequest).IsAssignableFrom(t) || !typeof(MediatR.IRequest<ServiceResult>).IsAssignableFrom(t))
            .Select(t => t.FullName).Order(StringComparer.Ordinal).ToList();
        wrong.Should().BeEmpty("宣告稽核的請求要是 ITransactionalRequest 且是 IRequest<ServiceResult>；不符：" + string.Join("、", wrong));
    }

    [Fact]
    public void Catalog_CodesUniqueAndCamelCase_GroupsKnown_FloorIsHighRisk()
    {
        AuditActions.All.Select(e => e.Code).Should().OnlyHaveUniqueItems();
        AuditActions.All.Should().OnlyContain(e => AuditActions.Groups.Contains(e.Group));
        AuditActions.All.Should().OnlyContain(e => System.Text.RegularExpressions.Regex.IsMatch(e.Code, "^[a-z][A-Za-z]*(\\.[a-z][A-Za-z]*)+$"), "代碼是 <分組或對象>.<動詞>、camelCase");
        // C7 地板：改權限、套用或刪除應用程式套件、刪模組或欄位；另加授予或拿掉系統管理員（使用者建立與修改）
        foreach (var code in new[] { "permission.update", "app.open", "app.import", "app.delete", "module.delete", "field.delete", "user.create", "user.update" })
            AuditActions.Find(code)!.Risk.Should().Be(AuditRiskLevel.HighRisk, code);
        AuditActions.CodesOf(AuditActions.Group).Should().Contain("group.setMembers").And.NotContain("user.update");
        AuditActions.CodesOf("nope").Should().BeEmpty();
    }

    private sealed record OverrideProbe : MediatR.IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
    {
        public AuditAction Audit => AuditAction.For("locale.pack.import", AuditTargets.Locale, null);
    }

    /// <summary>handler 換掉的動作代碼也要登記在目錄（語系包 i4d1 計畫判斷 17：新安裝換成 <c>locale.install</c>）；沒登記丟例外，不寫一列沒有標籤的稽核。</summary>
    [Fact]
    public async Task ActionOverride_MustBeRegistered()
    {
        var context = new AuditContext();
        var behavior = new AuditBehavior<OverrideProbe, ServiceResult>(context, null!, null!, null!);
        var act = () => behavior.Handle(new OverrideProbe(), () =>
        {
            context.Action("locale.nope");
            return Task.FromResult(ServiceResult.Success());
        }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*locale.nope*");
        context.ActionCode.Should().BeNull("請求結束就清掉");
    }
}
