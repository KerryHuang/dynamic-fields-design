using System.Reflection;
using System.Reflection.Emit;
using DynamicModules.Application.Common.Audit;
using FluentAssertions;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// A10 的架構部分（維運設計 §17.2.1、§17.9 安全）：<see cref="AuditOrigin.BeginSignIn"/> 的引用者只有兩個登入 handler；
/// 範圍內允許覆寫行為者，所以不能讓其他程式開這個範圍。掃產品組件的 IL：<c>call</c>、<c>callvirt</c>、<c>newobj</c>、
/// <c>ldftn</c>、<c>ldvirtftn</c> 都算（方法群組轉委派是 <c>ldftn</c>），編譯器產生的狀態機與 lambda 類別歸到最外層型別。
/// 放在 WebAPITests：這裡才載得到全部五個產品組件。
/// </summary>
public class AuditArchitectureTests
{
    private static readonly MethodInfo Target = typeof(AuditOrigin).GetMethod(nameof(AuditOrigin.BeginSignIn))!;

    private static readonly string[] Allowed =
    [
        "DynamicModules.Application.Dynamic.AccountService.Commands.DemoLogin.DemoLoginHandler",
        "DynamicModules.Application.Dynamic.AccountService.Commands.Login.LoginHandler",
    ];

    private static readonly Assembly[] Product =
    [
        typeof(DynamicModules.Domain.Data.DynamicDbContext).Assembly,
        typeof(AuditOrigin).Assembly,
        typeof(DynamicModules.Persistence.DatabaseErrors).Assembly,
        typeof(DynamicModules.Persistence.Postgres.PostgresModelCustomizer).Assembly,
        typeof(Program).Assembly,
    ];

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    private static readonly HashSet<short> MethodRefs =
        [OpCodes.Call.Value, OpCodes.Callvirt.Value, OpCodes.Newobj.Value, OpCodes.Ldftn.Value, OpCodes.Ldvirtftn.Value];

    /// <summary>組件裡引用 <see cref="AuditOrigin.BeginSignIn"/> 的最外層型別全名。</summary>
    internal static List<string> Callers(Assembly assembly)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return assembly.GetTypes()
            .SelectMany(t => t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            .Where(m => References(m).Any(r => r == Target))
            .Select(m => Outermost(m.DeclaringType!).FullName!)
            .Where(n => n != typeof(AuditOrigin).FullName)
            .Distinct().Order(StringComparer.Ordinal).ToList();
    }

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
                try { resolved = method.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs); }
                catch (Exception) { /* 解不開的泛型參考不會是 BeginSignIn（非泛型靜態方法） */ }
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
    public void BeginSignIn_IsReferencedOnlyByTheTwoLoginHandlers()
    {
        Product.Distinct().SelectMany(Callers).Order(StringComparer.Ordinal).Should().Equal(Allowed);
    }

    [Fact]
    public void Scanner_CatchesDirectCalls_MethodGroups_AndAsyncStateMachines()
    {
        // 測試替身在測試組件裡（不進產品組件）；掃描器要抓得到才代表上一個測試不會假綠
        Callers(typeof(AuditArchitectureTests).Assembly).Should().Equal(
            typeof(SneakyAsync).FullName, typeof(SneakyDirect).FullName, typeof(SneakyGroup).FullName);
    }
}

internal static class SneakyAsync
{
    public static async Task Run()
    {
        using var scope = AuditOrigin.BeginSignIn();
        await Task.Yield();
    }
}

internal static class SneakyDirect
{
    public static IDisposable Run() => AuditOrigin.BeginSignIn();
}

internal static class SneakyGroup
{
    public static Func<IDisposable> Run() => AuditOrigin.BeginSignIn;
}
