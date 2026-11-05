using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes;

/// <summary>各欄位型別能否設為可篩選、可排序；textarea 兩者皆不行，multienum、reference 可篩選不可排序；formula、autonumber 兩者皆可。</summary>
public static class FieldTypeCapabilities
{
    public static bool SupportsFilter(FieldType type) => type != FieldType.Textarea;

    public static bool SupportsSort(FieldType type) => type is not (FieldType.Textarea or FieldType.MultiEnum or FieldType.Reference);
}
