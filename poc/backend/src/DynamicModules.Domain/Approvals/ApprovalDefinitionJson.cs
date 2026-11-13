using System.Text.Json;

namespace DynamicModules.Domain.Approvals;

public static class ApprovalDefinitionJson
{
    /// <summary>兩份定義序列化後是否完全相同（不做順序正規化）；用來判斷存檔有沒有真的改到定義。</summary>
    public static bool SameAs(ApprovalDefinition a, ApprovalDefinition b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
}
