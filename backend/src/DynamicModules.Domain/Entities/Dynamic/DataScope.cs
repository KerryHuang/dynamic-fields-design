namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>宣告順序由窄到寬（Own &lt; Group &lt; All），權限合併時以大小比較取最寬。</summary>
public enum DataScope { Own, Group, All }
