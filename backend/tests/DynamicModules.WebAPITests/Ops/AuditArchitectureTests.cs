using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// A10 的架構部分（維運設計 §17.2.1、§17.9 安全）：登入稽核列（<c>SignInAudit</c>，行為者由呼叫端傳入、不取環境身分）只有兩個登入 handler 組得出來。
/// 鎖法：<c>SignInAudit</c>、<c>SignInActor</c>、<c>AuditEntry.SignIn</c> 都是 internal，Application 不開 InternalsVisibleTo，其他組件編譯不到；
/// 組件內掃 IL：<c>call</c>、<c>callvirt</c>、<c>newobj</c>、<c>ldftn</c>、<c>ldvirtftn</c>、<c>ldtoken</c> 都算（方法群組轉委派是 <c>ldftn</c>，
/// expression tree 是 <c>ldtoken</c>），編譯器產生的狀態機與 lambda 類別歸到最外層型別。放在 WebAPITests：這裡才載得到全部五個產品組件。
/// 不會假綠：兩個主測試都比對非空清單（需要正向命中）；<c>newobj</c> 與 init setter 由 <see cref="SignInMarker_IsCreatedOnlyBySignInAudit"/>
/// 命中 <c>SignInAudit</c> 自己證明，改成「應為空」就失去這個證明。<c>with</c> 複製標記（<c>&lt;Clone&gt;$</c>）掃不到，由 <c>AuditLog</c> 寫入時驗形狀擋下。
/// </summary>
public class AuditArchitectureTests
{
    private static readonly Assembly App = typeof(AuditOrigin).Assembly;
    private static readonly Type SignInAudit = App.GetType("DynamicModules.Application.Common.Audit.SignInAudit", throwOnError: true)!;
    private static readonly Type SignInActor = App.GetType("DynamicModules.Application.Common.Audit.SignInActor", throwOnError: true)!;
    private static readonly PropertyInfo EntrySignIn = typeof(AuditEntry).GetProperty("SignIn", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly Assembly[] Product =
    [
        typeof(DynamicModules.Domain.Data.DynamicDbContext).Assembly,
        App,
        typeof(DynamicModules.Persistence.DatabaseErrors).Assembly,
        typeof(DynamicModules.Persistence.Postgres.PostgresModelCustomizer).Assembly,
        typeof(Program).Assembly,
    ];

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    private static readonly HashSet<short> MethodRefs =
        [OpCodes.Call.Value, OpCodes.Callvirt.Value, OpCodes.Newobj.Value, OpCodes.Ldftn.Value, OpCodes.Ldvirtftn.Value, OpCodes.Ldtoken.Value];

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>組件裡引用任一 <paramref name="targets"/> 的最外層型別全名（目標自己的型別不算）。</summary>
    internal static List<string> Callers(Assembly assembly, IReadOnlyCollection<MethodBase> targets)
    {
        var own = targets.Select(t => Outermost(t.DeclaringType!)).ToHashSet();
        return assembly.GetTypes()
            .SelectMany(t => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
            .Where(m => References(m).Any(targets.Contains))
            .Select(m => Outermost(m.DeclaringType!))
            .Where(t => !own.Contains(t))
            .Select(t => t.FullName!)
            .Distinct().Order(StringComparer.Ordinal).ToList();
    }

    private static List<string> ProductCallers(IReadOnlyCollection<MethodBase> targets) =>
        Product.Distinct().SelectMany(a => Callers(a, targets)).Distinct().Order(StringComparer.Ordinal).ToList();

    private static Type Outermost(Type t)
    {
        while (t.DeclaringType is not null) t = t.DeclaringType;
        return t;
    }

    private static IEnumerable<MethodBase> References(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (Exception) { yield break; }
        if (il is null) yield break;
        var typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        var methodArgs = method is MethodInfo { IsGenericMethod: true } mi ? mi.GetGenericArguments() : null;
        for (var i = 0; i < il.Length;)
        {
            OpCode op;
            if (il[i] == 0xFE) { op = OpCodesByValue[unchecked((short)(0xFE00 | il[i + 1]))]; i += 2; }
            else { op = OpCodesByValue[il[i]]; i++; }
            if (MethodRefs.Contains(op.Value))
            {
                MethodBase? resolved = null;
                // ldtoken 的運算元也可能是型別或欄位，只取方法
                try { resolved = method.Module.ResolveMember(BitConverter.ToInt32(il, i), typeArgs, methodArgs) as MethodBase; }
                catch (Exception) { /* 解不開的泛型參考不會是掃描目標（都是非泛型方法） */ }
                if (resolved is not null) yield return resolved;
            }
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
        }
    }

    [Fact]
    public void SignInAudit_IsReferencedOnlyByTheTwoLoginHandlers()
    {
        ProductCallers(SignInAudit.GetMethods(All).Cast<MethodBase>().ToList()).Should().Equal(
            "DynamicModules.Application.Dynamic.AccountService.Commands.DemoLogin.DemoLoginHandler",
            "DynamicModules.Application.Dynamic.AccountService.Commands.Login.LoginHandler");
    }

    [Fact]
    public void SignInMarker_IsCreatedOnlyBySignInAudit()
    {
        // 標記本身（建構子、AuditEntry.SignIn 的 init）只有 SignInAudit 碰得到；AuditLog 只讀
        var writers = SignInActor.GetConstructors(All).Cast<MethodBase>().Append(EntrySignIn.SetMethod!).ToList();
        ProductCallers(writers).Should().Equal(SignInAudit.FullName);
    }

    [Fact]
    public void SignInApi_IsInvisibleOutsideTheApplicationAssembly()
    {
        SignInAudit.IsVisible.Should().BeFalse();
        SignInActor.IsVisible.Should().BeFalse();
        EntrySignIn.GetMethod!.IsAssembly.Should().BeTrue();
        EntrySignIn.SetMethod!.IsAssembly.Should().BeTrue();
        App.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Should().BeEmpty("開了 InternalsVisibleTo，其他組件就編譯得到 SignInAudit，掃描範圍也要跟著擴大");
    }

    [Fact]
    public void Scanner_CatchesDirectCalls_MethodGroups_Lambdas_ExpressionTrees_AndAsyncStateMachines()
    {
        // 測試替身在測試組件裡（不進產品組件）；掃描器要抓得到才代表上面的測試不會假綠
        Callers(typeof(AuditArchitectureTests).Assembly, [typeof(ScannerProbe).GetMethod(nameof(ScannerProbe.Target))!]).Should().Equal(
            typeof(SneakyAsync).FullName, typeof(SneakyDirect).FullName, typeof(SneakyExpression).FullName,
            typeof(SneakyGroup).FullName, typeof(SneakyLambda).FullName);
    }
}

/// <summary>掃描器自我檢查的目標（測試組件編譯不到 internal 的 SignInAudit，改用替身方法）。</summary>
internal static class ScannerProbe
{
    public static IDisposable Target() => AuditOrigin.Begin(AuditChannel.Ui);
}

internal static class SneakyAsync
{
    public static async Task Run()
    {
        using var scope = ScannerProbe.Target();
        await Task.Yield();
    }
}

internal static class SneakyDirect
{
    public static IDisposable Run() => ScannerProbe.Target();
}

internal static class SneakyGroup
{
    public static Func<IDisposable> Run() => ScannerProbe.Target;
}

internal static class SneakyLambda
{
    // 不捕捉變數的 lambda 編進 <>c 閉包類別
    public static Func<IDisposable> Run() => () => ScannerProbe.Target();
}

internal static class SneakyExpression
{
    // expression tree 只留 ldtoken，沒有 call
    public static Expression<Func<IDisposable>> Run() => () => ScannerProbe.Target();
}
