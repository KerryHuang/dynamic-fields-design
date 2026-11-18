using System.Text.RegularExpressions;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Common.Audit;

/// <summary>
/// A10 的架構部分（維運設計 §17.2.1、§17.9 安全）：<c>AuditOrigin.BeginSignIn</c> 的呼叫者只有兩個登入 handler；
/// 範圍內允許覆寫行為者，所以不能讓其他程式開這個範圍。掃描整個 backend/src 的原始碼。
/// </summary>
public class AuditArchitectureTests
{
    private static string Src()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DynamicModules.Application"))) dir = dir.Parent;
        return dir is null ? throw new DirectoryNotFoundException("找不到 backend") : Path.Combine(dir.FullName, "src");
    }

    [Fact]
    public void BeginSignIn_IsCalledOnlyByTheTwoLoginHandlers()
    {
        var callers = Directory.EnumerateFiles(Src(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bBeginSignIn\s*\("))
            .Select(Path.GetFileName)
            .Where(n => n != "AuditOrigin.cs")
            .Order(StringComparer.Ordinal)
            .ToList();
        callers.Should().Equal("DemoLoginHandler.cs", "LoginHandler.cs");
    }
}
