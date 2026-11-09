using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.SharedMasters;

/// <summary>
/// 再匯入已存在的共用主檔套件：合併，不回 409（設計 §9）。
/// 定義層（<see cref="MergeDefinition"/>）：依代號把匯入的模組、區塊、欄位、選項補進目前的定義，既有的一律保留；
/// 資料庫層（<see cref="MergeIntoDatabaseAsync"/>）：主檔已開啟或關閉時，依主檔名下對照列的持久代號只補不刪不覆寫，
/// 補的欄位走貢獻實例化的路徑、寫主檔名下的對照列；撞到貢獻者代號的加 _2、_3 避開並警告。
/// </summary>
public sealed class MasterMerger(
    IAppRepository apps, IModuleRepository modules, MasterSymbols symbols, TemplateInstantiator instantiator,
    ISender sender, MasterLifecycle lifecycle, IUnitOfWork uow)
{
    private static readonly IReadOnlyDictionary<string, long> None = new Dictionary<string, long>();

    private static string Label(I18nText text) => text.Resolve(I18nText.FallbackLocale);

    private static string TypeKept(string label, string code) => $"「{label}」（{code}）的型別和匯入的不同，保留既有的";

    private static bool Same<T>(T a, T b) =>
        JsonSerializer.Serialize(a, TemplateJson.Options) == JsonSerializer.Serialize(b, TemplateJson.Options);

    /// <summary>代號已被佔用就加 _2、_3…；回傳實際用的代號並記入佔用集合。</summary>
    private static string Avoid(string code, ISet<string> taken)
    {
        var candidate = code;
        for (var i = 2; !taken.Add(candidate); i++) candidate = $"{code}_{i}";
        return candidate;
    }

    private static void Collided(List<string> warnings, string what, string label, string code, string used)
    {
        if (code != used)
            warnings.Add($"{what}「{label}」的代號 {code} 和應用程式貢獻的代號相同，改用 {used}；引用原代號的套件開啟時會對不上（unbound）");
    }

    // ---- 定義層 ----

    /// <summary>目前的定義（匯入的那份，沒有就是內建那份）加上匯入的：manifest、群組與其他部分保留目前的，只補模組、區塊、欄位、選項。</summary>
    public static (AppPackageData Merged, List<string> Warnings) MergeDefinition(AppPackageData current, AppPackageData incoming)
    {
        var warnings = new List<string>();
        var result = current.Modules.ToList();
        foreach (var im in incoming.Modules)
        {
            var index = result.FindIndex(m => m.Code == im.Code);
            if (index >= 0) { result[index] = MergeModule(result[index], im, warnings); continue; }
            var kept = im.Permissions.Where(p => current.Groups.Any(g => g.Code == p.Group)).ToList();
            if (kept.Count < im.Permissions.Count) warnings.Add($"模組「{Label(im.Name)}」的權限用到沒合併的群組，已略過");
            result.Add(im with { Permissions = kept });
        }
        if (!Same(current.Groups, incoming.Groups)) warnings.Add("群組沒有合併，保留既有的");
        if (!Same(current.FieldAccess, incoming.FieldAccess) || !Same(current.OptionScopes, incoming.OptionScopes)
            || !Same(current.Reports, incoming.Reports))
            warnings.Add("欄位例外、選項範圍與報表沒有合併，保留既有的");
        return (current with { Modules = result }, warnings);
    }

    private static TemplateModule MergeModule(TemplateModule current, TemplateModule incoming, List<string> warnings)
    {
        var blocks = current.Blocks.Select(b => b with { Fields = b.Fields.ToList() }).ToList();
        var fields = blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Code);
        var optionCodes = blocks.SelectMany(b => b.Fields).SelectMany(f => f.Options).Select(o => o.Code).ToHashSet();
        foreach (var ib in incoming.Blocks)
        {
            var target = blocks.FirstOrDefault(b => b.Code == ib.Code);
            foreach (var f in ib.Fields)
            {
                if (fields.TryGetValue(f.Code, out var existing))
                {
                    if (existing.Type != f.Type) { warnings.Add(TypeKept(Label(existing.Label), f.Code)); continue; }
                    var added = f.Options.Where(o => existing.Options.All(x => x.Code != o.Code))
                        .Select(o => o with { Code = Avoid(o.Code, optionCodes) }).ToList();
                    if (added.Count == 0) continue;
                    var merged = existing with { Options = [.. existing.Options, .. added] };
                    var host = blocks.First(b => b.Fields.Contains(existing));
                    host.Fields[host.Fields.IndexOf(existing)] = merged;
                    fields[f.Code] = merged;
                    continue;
                }
                if (target is null)
                {
                    target = ib with { Fields = [] };
                    blocks.Add(target);
                }
                var field = f with { Options = f.Options.Select(o => o with { Code = Avoid(o.Code, optionCodes) }).ToList() };
                target.Fields.Add(field);
                fields[f.Code] = field;
            }
        }
        if (!Same(current.Rules, incoming.Rules) || !Same(current.Views, incoming.Views) || !Same(current.Permissions, incoming.Permissions))
            warnings.Add($"模組「{Label(current.Name)}」的規則、檢視與權限沒有合併，保留既有的");
        return current with { Blocks = blocks };
    }

    // ---- 資料庫層 ----

    /// <summary>補進已存在的主檔；失敗回傳錯誤（呼叫端在交易內，整個匯入回滾），成功回 null。會提交。</summary>
    public async Task<ServiceResult?> MergeIntoDatabaseAsync(App master, AppPackageData incoming, List<string> warnings, CancellationToken ct)
    {
        var bindings = await apps.GetBindingsAsync(master.Id, ct);
        var others = new List<AppBindings>();
        foreach (var other in (await apps.ListAsync(ct)).Where(a => a.Id != master.Id)) others.Add(await apps.GetBindingsAsync(other.Id, ct));

        // 對照：主檔自己的符號用「模組代號.代號」（和主檔名下的對照列同名），需要的其他主檔用「主檔 id/模組代號.代號」
        var required = new List<App>();
        foreach (var r in master.Requires.Masters)
            if (await apps.GetByKeyAsync(r.Id, ct) is { } m) required.Add(m);
        var seeds = await symbols.SeedsAsync(required, ct);
        var seedFields = new Dictionary<string, long>(seeds.Fields);
        var seedKeys = new Dictionary<string, string>(seeds.FieldKeys);
        var seedOptions = new Dictionary<string, long>(seeds.Options);
        var seedModules = new Dictionary<string, (long Id, string Key)>(seeds.Modules);
        var loaded = new Dictionary<string, Module>();
        foreach (var member in master.Modules)
            if (await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct) is { } module)
            {
                loaded[member.Code] = module;
                seedModules[member.Code] = (module.Id, module.Key);
                seedModules[$"{master.Key}/{member.Code}"] = (module.Id, module.Key);
                var keyOf = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id, f => f.Key);
                foreach (var (name, id) in bindings.Fields.Where(x => !x.Key.Contains('/') && x.Key.StartsWith(member.Code + ".", StringComparison.Ordinal)))
                {
                    seedFields[name] = id;
                    if (keyOf.TryGetValue(id, out var key)) seedKeys[name] = key;
                }
            }
        foreach (var (name, id) in bindings.Options.Where(x => !x.Key.Contains('/'))) seedOptions[name] = id;

        var inputs = new List<ContributionInput>();
        var optionAdds = new List<(string Member, long FieldId, TemplateOption Option)>();
        var touched = new HashSet<long>();
        foreach (var im in incoming.Modules)
        {
            if (!loaded.TryGetValue(im.Code, out var module))
            {
                warnings.Add($"主檔已開啟，新的模組「{Label(im.Name)}」沒有合併");
                continue;
            }
            var prefix = im.Code + ".";
            var ownPrefix = $"{master.Key}/{im.Code}.";
            Dictionary<string, long> Base(IReadOnlyDictionary<string, long> names, ICollection<long> exists) =>
                names.Where(x => !x.Key.Contains('/') && x.Key.StartsWith(prefix, StringComparison.Ordinal) && exists.Contains(x.Value))
                    .ToDictionary(x => x.Key[prefix.Length..], x => x.Value);
            HashSet<string> Taken(Dictionary<string, long> own, Func<AppBindings, IEnumerable<string>> names) =>
                own.Keys.Concat(others.SelectMany(names).Where(n => n.StartsWith(ownPrefix, StringComparison.Ordinal)).Select(n => n[ownPrefix.Length..]))
                    .ToHashSet(StringComparer.Ordinal);
            var dbFields = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
            var baseFields = Base(bindings.Fields, dbFields.Keys);
            var baseOptions = Base(bindings.Options, dbFields.Values.SelectMany(f => f.Options).Select(o => o.Id).ToHashSet());
            var baseBlocks = Base(bindings.Blocks, module.Blocks.Select(b => b.Id).ToHashSet());
            var takenFields = Taken(baseFields, b => b.Fields.Keys);
            var takenOptions = Taken(baseOptions, b => b.Options.Keys);
            var takenBlocks = Taken(baseBlocks, b => b.Blocks.Keys);
            var renamed = new Dictionary<string, string>();

            TemplateOption NewOption(TemplateOption o)
            {
                var code = Avoid(o.Code, takenOptions);
                Collided(warnings, "選項", Label(o.Label), o.Code, code);
                return o with { Code = code };
            }

            var pending = new List<(TemplateBlock Block, long? BlockId, List<TemplateField> Fields)>();
            foreach (var ib in im.Blocks)
            {
                long? blockId = baseBlocks.TryGetValue(ib.Code, out var bid) ? bid : null;
                var fresh = new List<TemplateField>();
                foreach (var f in ib.Fields)
                {
                    if (baseFields.TryGetValue(f.Code, out var fieldId))
                    {
                        var existing = dbFields[fieldId];
                        if (existing.Type != f.Type) { warnings.Add(TypeKept(Label(existing.LabelI18n), f.Code)); continue; }
                        foreach (var o in f.Options)
                        {
                            if (!baseOptions.TryGetValue(o.Code, out var optionId)) optionAdds.Add((im.Code, fieldId, NewOption(o)));
                            else if (existing.Options.All(x => x.Id != optionId))
                                warnings.Add($"選項「{Label(o.Label)}」的代號 {o.Code} 已是同模組另一個欄位的選項，沒有合併");
                        }
                        continue;
                    }
                    var code = Avoid(f.Code, takenFields);
                    Collided(warnings, "欄位", Label(f.Label), f.Code, code);
                    if (code != f.Code) renamed[f.Code] = code;
                    fresh.Add(f with { Code = code, Config = NotRequired(f, warnings), Options = f.Options.Select(NewOption).ToList() });
                }
                if (fresh.Count > 0) pending.Add((ib, blockId, fresh));
            }
            foreach (var (ib, blockId, fresh) in pending)
            {
                var blockCode = blockId is null ? Avoid(ib.Code, takenBlocks) : ib.Code;
                if (blockId is null) Collided(warnings, "區塊", Label(ib.Label), ib.Code, blockCode);
                inputs.Add(new ContributionInput(im.Code, module.Key, blockCode, ib.Label,
                    fresh.Select(f => Renamed(f, renamed)).ToList(), blockId, None, None, MasterBase: true));
                if (blockId is null) touched.Add(module.Id);
            }
        }

        if (inputs.Count > 0)
        {
            var external = seedModules.ToDictionary(x => x.Key, x => x.Value.Key);
            var instantiated = await instantiator.InstantiateAsync(new TemplatePackage(1, master.Key, master.NameI18n, null, [], null),
                new Dictionary<string, InstantiateModuleInput>(), new HashSet<string>(), ct,
                new AppInstantiation(new Dictionary<string, long>(), external, [], [],
                    new InstantiationSeeds(seedFields, seedKeys, seedOptions, seedModules), inputs));
            if (!instantiated.IsSuccess) return instantiated;
            var ids = ((InstantiateTemplateResponse)instantiated.Data!).Ids!;
            var existingBlocks = bindings.Blocks.Values.ToHashSet();
            apps.AddBindings(
                ids.FieldIds.Where(x => !seedFields.ContainsKey(x.Key)).Select(x => new AppField { AppId = master.Id, Name = x.Key, FieldId = x.Value }),
                ids.OptionIds.Where(x => !seedOptions.ContainsKey(x.Key)).Select(x => new AppOption { AppId = master.Id, Name = x.Key, OptionId = x.Value }),
                ids.BlockIds.Where(x => !existingBlocks.Contains(x.Value)).Select(x => new AppBlock { AppId = master.Id, Name = x.Key, BlockId = x.Value }));
        }
        foreach (var (member, fieldId, option) in optionAdds)
        {
            var added = await sender.Send(new AddOptionRequest(fieldId, option.Label, option.Color), ct);
            if (!added.IsSuccess) return added;
            apps.AddBindings([], [new AppOption { AppId = master.Id, Name = $"{member}.{option.Code}", OptionId = ((OptionResponse)added.Data!).Id }], []);
        }
        await uow.CommitAsync(ct);
        foreach (var moduleId in touched) await lifecycle.ResequenceBlocksAsync(moduleId, ct);
        await uow.CommitAsync(ct);
        return null;
    }

    /// <summary>主檔已有資料：補上的欄位一律非必填（§9 情況 2）。</summary>
    private static JsonElement NotRequired(TemplateField f, List<string> warnings)
    {
        if (JsonNode.Parse(f.Config.GetRawText()) is not JsonObject node || node["required"] is not JsonValue v
            || !v.TryGetValue<bool>(out var required) || !required)
            return f.Config;
        node.Remove("required");
        warnings.Add($"「{Label(f.Label)}」在匯入檔是必填，主檔已開啟、可能已有資料，補上時改成非必填");
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>補上的公式引用了被避撞改名的欄位時，跟著改成新代號。</summary>
    private static TemplateField Renamed(TemplateField f, IReadOnlyDictionary<string, string> renamed)
    {
        if (renamed.Count == 0 || TemplateGraph.FormulaOf(f) is not { } formula) return f;
        var node = (JsonObject)JsonNode.Parse(f.Config.GetRawText())!;
        node["formula"] = TemplateGraph.FormulaReference().Replace(formula,
            x => renamed.TryGetValue(x.Groups[1].Value, out var code) ? $"{{{code}}}" : x.Value);
        return f with { Config = JsonSerializer.SerializeToElement(node) };
    }
}
