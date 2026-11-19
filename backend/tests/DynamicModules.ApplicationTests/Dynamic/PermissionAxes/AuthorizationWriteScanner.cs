using System.Text;
using System.Text.RegularExpressions;

namespace DynamicModules.ApplicationTests.Dynamic.PermissionAxes;

/// <summary>一個要掃描的原始碼檔；<see cref="Path"/> 相對於 <c>backend/src</c>，一律用 <c>/</c>。</summary>
public sealed record ScanSource(string Path, string Text);

/// <summary>登記的批次方法（檔案＋方法名）。多載共用一個名字：每個同名成員各自照規則檢查。</summary>
public sealed record BatchMethod(string File, string Method);

/// <summary>開寫入許可的位置：檔案、所在方法、許可種類（<c>AuthorizationPermit.&lt;種類&gt;(…)</c>）。</summary>
public sealed record PermitSite(string File, string Method, string Kind);

/// <summary>清單測試的登記內容（權限軸設計 §7.4 清單測試 2、3、5、6、8）。正式清單與自測樣本各給一份。</summary>
public sealed record ScanRegistry
{
    /// <summary>守衛實體的 CLR 型別名（含只由守衛實體繼承的基底型別）。</summary>
    public required IReadOnlySet<string> GuardTypes { get; init; }
    /// <summary>模型裡全部實體的 CLR 型別名（判斷 <c>Set&lt;X&gt;</c> 的 X 是否具體）。</summary>
    public required IReadOnlySet<string> EntityTypes { get; init; }
    /// <summary>不是 EF 的 <c>Set&lt;X&gt;</c>（例如 ASP.NET 的 <c>Features.Set&lt;IFormFeature&gt;</c>）：檔案＋型別。</summary>
    public IReadOnlySet<(string File, string Type)> NonEfSetCalls { get; init; } = new HashSet<(string, string)>();
    public required IReadOnlySet<PermitSite> PermitSites { get; init; }
    public required IReadOnlySet<string> GuardSetFiles { get; init; }
    /// <summary>四個守衛批次入口與它們該呼叫的 <c>AuthorizationBatchEntry</c> 值。</summary>
    public required IReadOnlyDictionary<BatchMethod, string> GuardBatchEntries { get; init; }
    public required IReadOnlySet<BatchMethod> NonGuardBatchMethods { get; init; }
    public required IReadOnlySet<string> RawSqlFiles { get; init; }
    public required IReadOnlySet<string> WebApiDbFiles { get; init; }
}

/// <summary>掃描結果：違規（每筆一行，開頭是檔案路徑）與實際找到的登記項（給「恰為登記清單」的比對）。</summary>
public sealed class ScanResult
{
    public List<string> Violations { get; } = [];
    public HashSet<PermitSite> PermitSites { get; } = [];
    public HashSet<string> GuardSetFiles { get; } = [];
    public HashSet<BatchMethod> BatchMethods { get; } = [];
    public HashSet<string> RawSqlFiles { get; } = [];
    public HashSet<string> WebApiDbFiles { get; } = [];
}

/// <summary>
/// 授權寫入邊界的原始碼掃描器（權限軸設計 §7.4 清單測試 2、3、5、6、7、8 的文字部分；1、4、9 是反射與模型掃描，在測試類別裡）。
/// 先把 C# 原始碼分成「程式碼」「字串常值」「註解」三種，再對程式碼切出成員段落：
/// <list type="bullet">
/// <item>型別層級遇到 <c>{</c>：表頭（上一個 <c>; { }</c> 之後到這個 <c>{</c>）在第一個 <c>(</c> 之前有 class／struct／record／interface／enum／namespace
/// 就是型別；否則是成員（方法、建構子、有存取子的屬性），段落到對應的 <c>}</c>；表頭有 <c>=&gt;</c> 或 <c>=</c>（運算式主體、欄位初始值）
/// 時段落到型別層級的 <c>;</c>。沒有大括號的運算式主體成員、欄位、介面方法同樣到 <c>;</c>。</item>
/// <item>成員名：運算式主體只看 <c>=&gt;</c> 之前；有 <c>(</c> 的取第一個 <c>(</c> 前的識別字（略過泛型參數與 attribute）；
/// 第一個 <c>(</c> 前有指派的 <c>=</c> 是欄位，取 <c>=</c> 前的識別字；其餘取最後一個識別字。</item>
/// <item>local function、lambda 都算在外層成員裡；多載共用名字，各段各自檢查。</item>
/// <item>要看所在方法的呼叫（批次指令、開許可）落在任何成員段落之外（例如屬性初始值）時，整個檔案算違規（§23：切不準寧可誤擋）。</item>
/// </list>
/// </summary>
public static partial class AuthorizationWriteScanner
{
    private const string MigrationsSqlite = "DynamicModules.Persistence/Migrations/";
    private const string MigrationsPostgres = "DynamicModules.Persistence.Postgres/Migrations/";
    private const string ConfigurationsDir = "DynamicModules.Domain/Data/Configurations/";
    private const string AuthorizationTablesFile = "DynamicModules.Domain/Authorization/AuthorizationTables.cs";

    /// <summary>守衛表名與 <c>Dynamic_Field</c>（不分大小寫：SQLite 的識別字不分大小寫）；後面接識別字字元的不算（例如 <c>Dynamic_FieldOption</c>）。</summary>
    [GeneratedRegex(@"Dynamic_(GroupProgramPrivilege|UserProgramPrivilege|FieldPermission|ModuleDataScope|FieldOptionScope|FieldGrant|AppGrant|ModuleActionScope|UserGroupMapping|Field)(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase)]
    private static partial Regex GuardTableName();

    [GeneratedRegex(@"\bExecute(Delete|Update)(Async)?\b")]
    private static partial Regex BatchCall();

    [GeneratedRegex(@"\bAuthorizationWriteScope\s*\.\s*RunAsync\b")]
    private static partial Regex RunAsyncCall();

    [GeneratedRegex(@"\bAuthorizationPermit\s*\.\s*(\w+)\s*\(")]
    private static partial Regex PermitFactory();

    [GeneratedRegex(@"\b(RunForTestsAsync|SaveForTestsAsync|SaveForTests)\b|\bAuthorizationPermit\s*\.\s*ForTests\b")]
    private static partial Regex TestPermitUse();

    [GeneratedRegex(@"\busing\s+(static\s+)?(\w+\s*=\s*)?[\w.]*AuthorizationWriteScope\s*;")]
    private static partial Regex WriteScopeUsingAlias();

    [GeneratedRegex(@"\bSet\s*<\s*([\w.]+)\s*>")]
    private static partial Regex GenericSet();

    [GeneratedRegex(@"\bSet\s*\(\s*typeof\b")]
    private static partial Regex TypeofSet();

    [GeneratedRegex(@"\b(IQueryable|IOrderedQueryable|IIncludableQueryable|DbSet)\s*<\s*(?:\w+\s*\.\s*)*(\w+)\b")]
    private static partial Regex QueryableOf();

    [GeneratedRegex(@"\bExecuteSql\w*|\bFromSql\w*|\bSqlQuery\w*|\busing\s+Dapper\b|\bDapper\s*\.|\bCreateCommand\b|\bExecuteNonQuery\w*|\bExecuteReader\w*|\bExecuteScalar\w*|\b(Db|Npgsql|Sqlite)(Command|Batch)\b")]
    private static partial Regex RawSqlUse();

    [GeneratedRegex(@"\.\s*(Sql|InsertData|UpdateData|DeleteData)\s*\(")]
    private static partial Regex MigrationDataOp();

    [GeneratedRegex(@"\b\w*DbContext\b|\bDbSet\b")]
    private static partial Regex DbContextUse();

    [GeneratedRegex(@"\bAuthorizationTables\b|\bGetTableName\b")]
    private static partial Regex TableNameLookup();

    [GeneratedRegex(@"\b(class|struct|interface|enum|record|namespace)\b")]
    private static partial Regex TypeKeyword();

    [GeneratedRegex(@"\b(public|protected|internal)\b")]
    private static partial Regex NonPrivateModifier();

    [GeneratedRegex(@"\bAuthorizationWriteScope\s*\.\s*Demand\s*\(\s*(?:[\w.]*\.)?AuthorizationBatchEntry\s*\.\s*(\w+)")]
    private static partial Regex DemandCall();

    [GeneratedRegex(@"\b(VisibilityRestricted|ExcludeFromAi)\b")]
    private static partial Regex GuardColumn();

    public static ScanResult Scan(IEnumerable<ScanSource> sources, ScanRegistry registry)
    {
        var result = new ScanResult();
        var lexed = sources.Select(s => new Lexed(s).WithRegistry(registry)).ToList();
        foreach (var file in lexed)
        {
            CheckPermitSites(file, registry, result);
            CheckGuardSets(file, registry, result);
            CheckBatchCalls(file, registry, result);
            CheckRawSql(file, registry, result);
            CheckMigrations(file, result);
            CheckLayers(file, registry, result);
        }
        return result;
    }

    /// <summary>只留程式碼（註解與字串常值的內容換成空白）。</summary>
    public static string CodeOf(string text) => new Lexed(new ScanSource("", text)).Code;

    /// <summary>登記了、但這次掃描沒找到的項目（清單過期也算失敗：清單要和程式一致）。</summary>
    public static List<string> Stale(ScanResult found, ScanRegistry registry)
    {
        var stale = new List<string>();
        stale.AddRange(registry.PermitSites.Except(found.PermitSites).Select(s => $"登記的開許可位置不存在：{s}"));
        stale.AddRange(registry.GuardSetFiles.Except(found.GuardSetFiles).Select(f => $"登記的 Set<守衛型別> 檔案沒有用到：{f}"));
        stale.AddRange(registry.GuardBatchEntries.Keys.Concat(registry.NonGuardBatchMethods).Except(found.BatchMethods)
            .Select(m => $"登記的批次方法不存在：{m}"));
        stale.AddRange(registry.RawSqlFiles.Except(found.RawSqlFiles).Select(f => $"登記的原生 SQL 檔案沒有用到：{f}"));
        stale.AddRange(registry.WebApiDbFiles.Except(found.WebApiDbFiles).Select(f => $"登記的 WebAPI DbContext 檔案沒有用到：{f}"));
        return stale;
    }

    // ── 清單測試 2：開許可的位置 ──────────────────────────────────────────────

    private static void CheckPermitSites(Lexed f, ScanRegistry r, ScanResult result)
    {
        foreach (Match m in TestPermitUse().Matches(f.Code))
            result.Violations.Add($"{f.Path}: src 不得使用測試許可（{m.Value.Trim()}）");
        foreach (Match m in WriteScopeUsingAlias().Matches(f.Code))
            result.Violations.Add($"{f.Path}: 不得以 using static／別名引用 AuthorizationWriteScope（開許可的位置要能被掃到）");

        foreach (Match m in RunAsyncCall().Matches(f.Code))
        {
            var seg = f.SegmentAt(m.Index);
            if (seg is null)
            {
                result.Violations.Add($"{f.Path}: AuthorizationWriteScope.RunAsync 在成員段落之外，整個檔案算違規");
                continue;
            }
            var open = f.Code.IndexOf('(', m.Index + m.Length);
            var afterName = m.Index + m.Length;
            var args = open < 0 || f.Code[afterName..open].Trim().Length > 0 ? "" : f.Code[open..f.MatchParen(open)];
            var kinds = PermitFactory().Matches(args).Select(k => k.Groups[1].Value).Distinct().ToList();
            if (kinds.Count != 1)
            {
                result.Violations.Add($"{f.Path}: {seg.Name} 的 AuthorizationWriteScope.RunAsync 看不出許可種類（要直接寫 AuthorizationPermit.<種類>(…)）");
                continue;
            }
            var site = new PermitSite(f.Path, seg.Name, kinds[0]);
            result.PermitSites.Add(site);
            if (!r.PermitSites.Contains(site)) result.Violations.Add($"{f.Path}: 未登記的開許可位置 {site}");
        }
    }

    // ── 清單測試 3：守衛實體的取得點 ──────────────────────────────────────────

    private static void CheckGuardSets(Lexed f, ScanRegistry r, ScanResult result)
    {
        foreach (Match m in GenericSet().Matches(f.Code))
        {
            var type = m.Groups[1].Value.Split('.')[^1];
            if (r.GuardTypes.Contains(type))
            {
                result.GuardSetFiles.Add(f.Path);
                if (!r.GuardSetFiles.Contains(f.Path)) result.Violations.Add($"{f.Path}: 未登記的檔案取 Set<{type}>");
            }
            else if (!r.EntityTypes.Contains(type) && !r.NonEfSetCalls.Contains((f.Path, type)))
                result.Violations.Add($"{f.Path}: Set<{type}> 的型別參數不是具體的實體型別");
        }
        foreach (Match m in TypeofSet().Matches(f.Code))
            result.Violations.Add($"{f.Path}: 不得以 Set(typeof…) 取實體集合");
        foreach (Match m in QueryableOf().Matches(f.Code))
            if (r.GuardTypes.Contains(m.Groups[2].Value))
                result.Violations.Add($"{f.Path}: 不得以 {m.Groups[1].Value}<{m.Groups[2].Value}> 交出守衛型別的查詢（要具體化成 List／IReadOnlyList）");
    }

    // ── 清單測試 5：批次指令 ─────────────────────────────────────────────────

    private static void CheckBatchCalls(Lexed f, ScanRegistry r, ScanResult result)
    {
        var bySegment = new Dictionary<Segment, List<int>>();
        foreach (Match m in BatchCall().Matches(f.Code))
        {
            var seg = f.SegmentAt(m.Index);
            if (seg is null)
            {
                result.Violations.Add($"{f.Path}: 批次指令 {m.Value} 在成員段落之外，整個檔案算違規");
                continue;
            }
            if (!bySegment.TryGetValue(seg, out var list)) bySegment[seg] = list = [];
            list.Add(m.Index);
        }

        foreach (var (seg, calls) in bySegment)
        {
            var method = new BatchMethod(f.Path, seg.Name);
            result.BatchMethods.Add(method);
            var isEntry = r.GuardBatchEntries.TryGetValue(method, out var entry);
            if (!isEntry && !r.NonGuardBatchMethods.Contains(method))
            {
                result.Violations.Add($"{f.Path}: 未登記的批次方法 {seg.Name}");
                continue;
            }

            if (isEntry)
            {
                var demand = DemandCall().Matches(f.Code[seg.Start..seg.End]).FirstOrDefault(d => d.Groups[1].Value == entry);
                if (demand is null || seg.Start + demand.Index > calls.Min())
                    result.Violations.Add($"{f.Path}: 守衛批次入口 {seg.Name} 要在第一個寫入前呼叫 Demand(AuthorizationBatchEntry.{entry})");
            }

            foreach (var at in calls)
            {
                var why = Untraceable(f, seg, at, allowGuard: isEntry);
                if (why is not null) result.Violations.Add($"{f.Path}: {seg.Name} 的批次指令{why}");
            }

            if (calls.Any(at => f.Code[at..].StartsWith("ExecuteUpdate", StringComparison.Ordinal))
                && GuardColumn().Match(f.NoComments[seg.Start..seg.End]) is { Success: true } col)
                result.Violations.Add($"{f.Path}: {seg.Name} 呼叫 ExecuteUpdate*，方法內不得出現守衛欄 {col.Value}");
        }
    }

    /// <summary>接收者追不到同方法內、或同檔案私有屬性／欄位定義裡的 <c>Set&lt;具體型別&gt;()</c> 就回原因；追到守衛型別而且不是入口也回原因。</summary>
    private static string? Untraceable(Lexed f, Segment seg, int callAt, bool allowGuard)
    {
        var dot = f.SkipSpaceBack(callAt - 1);
        if (dot < 0 || f.Code[dot] != '.') return "沒有以 .Execute*(…) 的形式接在查詢後面";
        var start = f.ChainStart(dot);
        if (start < 0) return "的接收者無法解析";
        var type = TraceExpression(f, seg, f.Code[start..dot], depth: 0);
        if (type is null) return $"的接收者（{Squash(f.Code[start..dot])}）追不到同方法或同檔案私有成員裡的 Set<具體型別>()";
        if (!allowGuard && type.Value.Guard) return $"的接收者追到守衛型別 Set<{type.Value.Name}>()（只准四個守衛批次入口）";
        return null;
    }

    [GeneratedRegex(@"^\s*(?:(?:this|[A-Za-z_]\w*)\s*\.\s*)?Set\s*<\s*([\w.]+)\s*>\s*\(\s*\)")]
    private static partial Regex RootSet();

    [GeneratedRegex(@"^\s*(?:this\s*\.\s*)?([A-Za-z_]\w*)\b(?!\s*[(<])")]
    private static partial Regex RootIdentifier();

    private static (string Name, bool Guard)? TraceExpression(Lexed f, Segment seg, string expr, int depth)
    {
        if (depth > 5) return null;
        if (RootSet().Match(expr) is { Success: true } set)
        {
            var name = set.Groups[1].Value.Split('.')[^1];
            return f.Registry.EntityTypes.Contains(name) ? (name, f.Registry.GuardTypes.Contains(name)) : null;
        }
        if (RootIdentifier().Match(expr) is not { Success: true } id) return null;
        var ident = id.Groups[1].Value;

        // 同方法內的區域變數：每一次指派都要追得到，型別取第一個（守衛型別只要出現就算）
        var assignments = Regex.Matches(f.Code[seg.Start..seg.End], $@"(?<![\w.])(?:var\s+)?{Regex.Escape(ident)}\s*=(?![=>])")
            .Select(a => seg.Start + a.Index + a.Length).ToList();
        if (assignments.Count > 0)
        {
            (string Name, bool Guard)? first = null;
            foreach (var at in assignments)
            {
                var traced = TraceExpression(f, seg, f.Code[at..f.StatementEnd(at)], depth + 1);
                if (traced is null) return null;
                if (first is null || traced.Value.Guard) first = traced;
            }
            return first;
        }

        // 同檔案的私有屬性或欄位（方法回傳值、參數、別的檔案的成員都不追）
        var member = f.Segments.Where(s => s.Kind != SegmentKind.Method && s.Name == ident && s.IsPrivate).ToList();
        if (member.Count != 1) return null;
        var body = member[0].Body(f);
        return body is null ? null : TraceExpression(f, member[0], body, depth + 1);
    }

    // ── 清單測試 6：原生 SQL 與表名字串 ──────────────────────────────────────

    private static bool IsMigration(string path) => path.StartsWith(MigrationsSqlite, StringComparison.Ordinal) || path.StartsWith(MigrationsPostgres, StringComparison.Ordinal);

    private static void CheckRawSql(Lexed f, ScanRegistry r, ScanResult result)
    {
        var migration = IsMigration(f.Path);
        if (!migration && RawSqlUse().Match(f.Code) is { Success: true } use)
        {
            result.RawSqlFiles.Add(f.Path);
            if (!r.RawSqlFiles.Contains(f.Path)) result.Violations.Add($"{f.Path}: 未登記的原生 SQL 檔案（{use.Value.Trim()}）");
            else if (TableNameLookup().Match(f.Code) is { Success: true } lookup)
                result.Violations.Add($"{f.Path}: 原生 SQL 檔案不得取用表名（{lookup.Value}），否則掃不到它在執行期拼出的守衛表");
            else if (r.GuardTypes.FirstOrDefault(g => Regex.IsMatch(f.Code, $@"\b{g}\b")) is { } guardType)
                result.Violations.Add($"{f.Path}: 原生 SQL 檔案不得引用守衛型別 {guardType}");
        }

        var exempt = migration || f.Path.StartsWith(ConfigurationsDir, StringComparison.Ordinal) || f.Path == AuthorizationTablesFile;
        if (exempt) return;
        foreach (var literal in f.Literals)
            if (GuardTableName().Match(literal) is { Success: true } t)
                result.Violations.Add($"{f.Path}: 字串常值含守衛表名 {t.Value}");
    }

    // ── 清單測試 7：migration 不得寫守衛表的資料 ─────────────────────────────

    private static void CheckMigrations(Lexed f, ScanResult result)
    {
        if (!IsMigration(f.Path) || f.Path.EndsWith(".Designer.cs", StringComparison.Ordinal) || f.Path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)) return;
        foreach (Match m in MigrationDataOp().Matches(f.Code))
        {
            var open = m.Index + m.Length - 1;
            var args = f.NoComments[open..f.MatchParen(open)];
            if (GuardTableName().Match(args) is { Success: true } t)
                result.Violations.Add($"{f.Path}: migration 不得以 {m.Groups[1].Value} 寫 {t.Value} 的資料");
            else if (m.Groups[1].Value == "Sql" && !args.Contains('"'))
                result.Violations.Add($"{f.Path}: migration 的 Sql 要是字串常值（看不出寫哪張表，寧可誤擋）");
        }
    }

    // ── 清單測試 8：分層 ──────────────────────────────────────────────────────

    private static void CheckLayers(Lexed f, ScanRegistry r, ScanResult result)
    {
        if (f.Path.StartsWith("DynamicModules.Application/", StringComparison.Ordinal) && DbContextUse().Match(f.Code) is { Success: true } app)
            result.Violations.Add($"{f.Path}: Application 不得引用 {app.Value}");
        if (!f.Path.StartsWith("DynamicModules.WebAPI/", StringComparison.Ordinal) || !DbContextUse().IsMatch(f.Code)) return;
        result.WebApiDbFiles.Add(f.Path);
        if (!r.WebApiDbFiles.Contains(f.Path)) result.Violations.Add($"{f.Path}: WebAPI 只准登記的檔案引用 DbContext");
        else if (r.GuardTypes.FirstOrDefault(g => Regex.IsMatch(f.Code, $@"\b{g}\b")) is { } guardType)
            result.Violations.Add($"{f.Path}: WebAPI 的登記檔案不得出現守衛型別 {guardType}");
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim() is var t && t.Length > 80 ? t[..80] + "…" : Regex.Replace(s, @"\s+", " ").Trim();

    // ── 詞法與成員切段 ────────────────────────────────────────────────────────

    private enum SegmentKind { Method, Property, Field }

    private sealed record Segment(string Name, SegmentKind Kind, int Start, int End, int HeaderEnd, bool IsPrivate)
    {
        /// <summary>屬性或欄位的運算式（<c>=&gt;</c> 或 <c>=</c> 之後），給接收者追蹤用。</summary>
        public string? Body(Lexed f)
        {
            var text = f.Code[Start..End];
            var arrow = text.IndexOf("=>", StringComparison.Ordinal);
            if (arrow >= 0) return text[(arrow + 2)..];
            var eq = Regex.Match(text, @"(?<![=!<>])=(?![=>])");
            return eq.Success ? text[(eq.Index + 1)..] : null;
        }
    }

    private sealed class Lexed
    {
        public string Path { get; }
        /// <summary>註解換成空白、字串保留。</summary>
        public string NoComments { get; }
        /// <summary>註解與字串／字元常值的內容換成空白（插值的程式碼保留），長度與原文相同。</summary>
        public string Code { get; }
        public List<string> Literals { get; } = [];
        public List<Segment> Segments { get; } = [];
        public ScanRegistry Registry { get; private set; } = null!;

        private readonly string _raw;
        private readonly char[] _nc;
        private readonly char[] _code;

        public Lexed(ScanSource source)
        {
            Path = source.Path;
            _raw = source.Text;
            _nc = _raw.ToCharArray();
            _code = _raw.ToCharArray();
            var end = LexCode(0, 0);
            if (end < _raw.Length) Blank(_code, end, _raw.Length);
            NoComments = new string(_nc);
            Code = new string(_code);
            Split();
        }

        public Lexed WithRegistry(ScanRegistry r) { Registry = r; return this; }

        private static void Blank(char[] a, int from, int to)
        {
            for (var i = from; i < to && i < a.Length; i++) if (a[i] != '\n' && a[i] != '\r') a[i] = ' ';
        }

        private char At(int i) => i < _raw.Length ? _raw[i] : '\0';

        /// <summary>從 i 開始掃程式碼；holeBraces &gt; 0 時是插值字串的 hole，遇到深度 0 的 <c>}</c> 就回它的位置。</summary>
        private int LexCode(int i, int holeBraces)
        {
            var depth = 0;
            while (i < _raw.Length)
            {
                var c = _raw[i];
                if (c == '/' && At(i + 1) == '/')
                {
                    var j = _raw.IndexOf('\n', i);
                    if (j < 0) j = _raw.Length;
                    Blank(_nc, i, j); Blank(_code, i, j);
                    i = j;
                    continue;
                }
                if (c == '/' && At(i + 1) == '*')
                {
                    var j = _raw.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    j = j < 0 ? _raw.Length : j + 2;
                    Blank(_nc, i, j); Blank(_code, i, j);
                    i = j;
                    continue;
                }
                if (c == '\'')
                {
                    var j = i + 1;
                    while (j < _raw.Length && _raw[j] != '\'' && _raw[j] != '\n') j += _raw[j] == '\\' ? 2 : 1;
                    Blank(_code, i + 1, j);
                    i = j + 1;
                    continue;
                }
                if ((c == '"' || c == '@' || c == '$') && TryString(i) is { } after)
                {
                    i = after;
                    continue;
                }
                if (holeBraces > 0)
                {
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        if (depth == 0) return i;
                        depth--;
                    }
                }
                i++;
            }
            return i;
        }

        /// <summary>i 是字串的開頭（<c>"</c>、<c>@"</c>、<c>$"</c>、<c>$@"</c>、<c>@$"</c>、<c>"""</c>、<c>$$"""</c>…）就回字串結束後的位置，否則 null。</summary>
        private int? TryString(int i)
        {
            var k = i;
            var dollars = 0;
            var verbatim = false;
            while (At(k) == '$') { dollars++; k++; }
            if (At(k) == '@') { verbatim = true; k++; while (At(k) == '$') { dollars++; k++; } }
            if (At(k) != '"') return null;
            if (k > i && i > 0 && (char.IsLetterOrDigit(_raw[i - 1]) || _raw[i - 1] == '_')) return null;

            var quotes = 0;
            while (At(k + quotes) == '"') quotes++;
            var text = new StringBuilder();
            if (!verbatim && quotes >= 3) return RawString(k, quotes, dollars, text);
            if (!verbatim && quotes == 2) { Literals.Add(""); return k + 2; }

            var j = k + 1;
            var pieceStart = j;
            while (j < _raw.Length)
            {
                var c = _raw[j];
                if (!verbatim && c == '\\') { j += 2; continue; }
                if (c == '"')
                {
                    if (verbatim && At(j + 1) == '"') { j += 2; continue; }
                    break;
                }
                if (!verbatim && c == '\n') break;
                if (dollars > 0 && c == '{')
                {
                    if (At(j + 1) == '{') { j += 2; continue; }
                    text.Append(_raw, pieceStart, j - pieceStart).Append(' ');
                    Blank(_code, pieceStart, j + 1);
                    var close = LexCode(j + 1, 1);
                    j = close + 1;
                    pieceStart = j;
                    Blank(_code, close, close + 1);
                    continue;
                }
                j++;
            }
            text.Append(_raw, pieceStart, Math.Max(0, Math.Min(j, _raw.Length) - pieceStart));
            Blank(_code, pieceStart, j);
            Literals.Add(text.ToString());
            return Math.Min(j + 1, _raw.Length);
        }

        private int RawString(int k, int quotes, int dollars, StringBuilder text)
        {
            var fence = new string('"', quotes);
            var j = k + quotes;
            var pieceStart = j;
            while (j < _raw.Length)
            {
                if (string.CompareOrdinal(_raw, j, fence, 0, quotes) == 0) break;
                if (dollars > 0 && _raw[j] == '{')
                {
                    var run = 0;
                    while (At(j + run) == '{') run++;
                    if (run >= dollars)
                    {
                        text.Append(_raw, pieceStart, j - pieceStart).Append(' ');
                        Blank(_code, pieceStart, j + run);
                        var close = LexCode(j + run, dollars);
                        var closeRun = 0;
                        while (At(close + closeRun) == '}' && closeRun < dollars) closeRun++;
                        Blank(_code, close, close + closeRun);
                        j = close + closeRun;
                        pieceStart = j;
                        continue;
                    }
                    j += run;
                    continue;
                }
                j++;
            }
            text.Append(_raw, pieceStart, Math.Min(j, _raw.Length) - pieceStart);
            Blank(_code, pieceStart, j);
            Literals.Add(text.ToString());
            return Math.Min(j + quotes, _raw.Length);
        }

        private enum Frame { Type, Member, Other }

        /// <summary>以大括號結構切出成員段落（見類別說明）。</summary>
        private void Split()
        {
            var stack = new Stack<(Frame Kind, int Start, string Name, SegmentKind SegKind, bool Private, int HeaderEnd)>();
            (int Start, string Name, SegmentKind Kind, bool Private, int HeaderEnd, int Depth)? pendingExpr = null;
            var stmtStart = 0;
            var paren = 0;
            bool AtTypeLevel() => stack.Count == 0 || stack.Peek().Kind == Frame.Type;

            for (var i = 0; i < Code.Length; i++)
            {
                var c = Code[i];
                if (c is '(' or '[') { paren++; continue; }
                if (c is ')' or ']') { paren = Math.Max(0, paren - 1); continue; }
                if (paren > 0) continue;

                if (c == '{')
                {
                    if (AtTypeLevel() && pendingExpr is null)
                    {
                        var header = Header(stmtStart, i);
                        if (IsTypeHeader(header)) stack.Push((Frame.Type, i, "", SegmentKind.Method, false, i));
                        else if (header.Contains("=>") || HasAssignmentBeforeParen(header))
                        {
                            if (MemberName(header) is { } n)
                                pendingExpr = (SkipSpace(stmtStart), n.Name, n.Kind, n.Private, i, stack.Count);
                            stack.Push((Frame.Other, i, "", SegmentKind.Method, false, i));
                        }
                        else if (MemberName(header) is { } n)
                            stack.Push((Frame.Member, SkipSpace(stmtStart), n.Name, n.Kind, n.Private, i));
                        else stack.Push((Frame.Other, i, "", SegmentKind.Method, false, i));
                    }
                    else stack.Push((Frame.Other, i, "", SegmentKind.Method, false, i));
                    stmtStart = i + 1;
                }
                else if (c == '}')
                {
                    if (stack.Count > 0)
                    {
                        var top = stack.Pop();
                        if (top.Kind == Frame.Member) Segments.Add(new Segment(top.Name, top.SegKind, top.Start, i + 1, top.HeaderEnd, top.Private));
                    }
                    stmtStart = i + 1;
                }
                else if (c == ';')
                {
                    if (pendingExpr is { } p && stack.Count == p.Depth)
                    {
                        Segments.Add(new Segment(p.Name, p.Kind, p.Start, i + 1, p.HeaderEnd, p.Private));
                        pendingExpr = null;
                    }
                    else if (AtTypeLevel() && pendingExpr is null)
                    {
                        var header = Header(stmtStart, i);
                        if (!IsTypeHeader(header) && (header.Contains('(') || header.Contains("=>") || HasAssignmentBeforeParen(header))
                            && MemberName(header) is { } n)
                            Segments.Add(new Segment(n.Name, n.Kind, SkipSpace(stmtStart), i + 1, i, n.Private));
                    }
                    stmtStart = i + 1;
                }
            }
        }

        private int SkipSpace(int i)
        {
            while (i < Code.Length && char.IsWhiteSpace(Code[i])) i++;
            return i;
        }

        /// <summary>表頭：去掉前置 attribute 與前置處理器指示。</summary>
        private string Header(int from, int to)
        {
            var lines = Code[from..to].Split('\n').Where(l => !l.TrimStart().StartsWith('#'));
            var h = string.Join('\n', lines).Trim();
            while (h.StartsWith('['))
            {
                var depth = 0;
                var k = 0;
                for (; k < h.Length; k++)
                {
                    if (h[k] == '[') depth++;
                    else if (h[k] == ']' && --depth == 0) break;
                }
                h = k + 1 < h.Length ? h[(k + 1)..].Trim() : "";
            }
            return h;
        }

        private static bool IsTypeHeader(string header)
        {
            var paren = header.IndexOf('(');
            return TypeKeyword().IsMatch(paren < 0 ? header : header[..paren]);
        }

        private static bool HasAssignmentBeforeParen(string header)
        {
            var paren = header.IndexOf('(');
            var head = paren < 0 ? header : header[..paren];
            return Regex.IsMatch(head, @"(?<![=!<>])=(?![=>])");
        }

        private static (string Name, SegmentKind Kind, bool Private)? MemberName(string header)
        {
            var isPrivate = Regex.IsMatch(header, @"\bprivate\b") || !NonPrivateModifier().IsMatch(header);
            var h = header;
            var arrow = h.IndexOf("=>", StringComparison.Ordinal);
            if (arrow >= 0) h = h[..arrow];
            var assign = Regex.Match(h, @"(?<![=!<>])=(?![=>])");
            var paren = h.IndexOf('(');
            if (assign.Success && (paren < 0 || assign.Index < paren))
                return LastIdentifier(h[..assign.Index]) is { } f ? (f, SegmentKind.Field, isPrivate) : null;
            if (paren >= 0)
            {
                var before = h[..paren].TrimEnd();
                if (before.EndsWith('>'))
                {
                    var depth = 0;
                    var k = before.Length - 1;
                    for (; k >= 0; k--)
                    {
                        if (before[k] == '>') depth++;
                        else if (before[k] == '<' && --depth == 0) break;
                    }
                    before = k > 0 ? before[..k].TrimEnd() : "";
                }
                return Regex.Match(before, @"([A-Za-z_]\w*)$") is { Success: true } m ? (m.Groups[1].Value, SegmentKind.Method, isPrivate) : null;
            }
            return LastIdentifier(h) is { } p ? (p, SegmentKind.Property, isPrivate) : null;
        }

        private static string? LastIdentifier(string s) => Regex.Match(s.TrimEnd(), @"([A-Za-z_]\w*)\s*$") is { Success: true } m ? m.Groups[1].Value : null;

        public Segment? SegmentAt(int pos) => Segments.Where(s => s.Start <= pos && pos < s.End).OrderBy(s => s.End - s.Start).FirstOrDefault();

        /// <summary>open 是 <c>(</c>，回對應的 <c>)</c> 之後的位置。</summary>
        public int MatchParen(int open)
        {
            var depth = 0;
            for (var i = open; i < Code.Length; i++)
            {
                if (Code[i] == '(') depth++;
                else if (Code[i] == ')' && --depth == 0) return i + 1;
            }
            return Code.Length;
        }

        public int SkipSpaceBack(int i)
        {
            while (i >= 0 && char.IsWhiteSpace(Code[i])) i--;
            return i;
        }

        /// <summary>從 dot（呼叫鏈裡最後一個 <c>.</c>）往回找整條呼叫鏈的開頭：識別字、<c>.</c>、<c>?.</c>、<c>!</c>、成對的括號與泛型角括號。</summary>
        public int ChainStart(int dot)
        {
            var i = dot - 1;
            while (true)
            {
                i = SkipSpaceBack(i);
                if (i < 0) return -1;
                if (Code[i] == '!') i = SkipSpaceBack(i - 1);
                if (Code[i] == ')')
                {
                    i = MatchBack(i, '(', ')');
                    if (i < 0) return -1;
                    var j = SkipSpaceBack(i - 1);
                    if (j >= 0 && Code[j] == '>') { j = MatchBack(j, '<', '>'); if (j < 0) return -1; j = SkipSpaceBack(j - 1); }
                    if (j < 0 || !IsIdent(Code[j])) return i;     // 括號包住的運算式：開頭就是括號
                    i = j;
                }
                if (Code[i] == ']') { i = MatchBack(i, '[', ']'); if (i < 0) return -1; i--; continue; }
                if (!IsIdent(Code[i])) return -1;
                while (i > 0 && IsIdent(Code[i - 1])) i--;
                var start = i;
                var before = SkipSpaceBack(i - 1);
                if (before >= 0 && Code[before] == '.')
                {
                    i = before - 1;
                    if (i >= 0 && Code[i] == '?') i--;
                    continue;
                }
                return start;
            }
        }

        private int MatchBack(int close, char openCh, char closeCh)
        {
            var depth = 0;
            for (var i = close; i >= 0; i--)
            {
                if (Code[i] == closeCh) depth++;
                else if (Code[i] == openCh && --depth == 0) return i;
            }
            return -1;
        }

        private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>從 at 起到同層的 <c>;</c>（或段落結束）為止。</summary>
        public int StatementEnd(int at)
        {
            var depth = 0;
            for (var i = at; i < Code.Length; i++)
            {
                var c = Code[i];
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') { if (--depth < 0) return i; }
                else if ((c == ';' || c == ',') && depth == 0) return i;
            }
            return Code.Length;
        }
    }
}
