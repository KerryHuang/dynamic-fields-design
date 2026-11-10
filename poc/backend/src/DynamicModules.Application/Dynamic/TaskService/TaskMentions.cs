using System.Text.RegularExpressions;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>留言內文的 @ 標記是 <c>@{userId}</c>；格式不對的（<c>@{</c>、<c>@{}</c>、<c>@{a b}</c>、<c>@user</c>）不算標記，照原文顯示。</summary>
public static partial class TaskMentions
{
    [GeneratedRegex(@"@\{([A-Za-z0-9._\-]{1,50})\}")]
    private static partial Regex Pattern();

    /// <summary>依出現順序去重；帳號一律小寫儲存，所以轉成小寫再比對（<c>@{Sales01}</c> 就是 sales01）。</summary>
    public static IReadOnlyList<string> UserIds(string body) =>
        Pattern().Matches(body).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
}
