namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>七個動作軸，順序與 API 名稱見第 3 階段計畫的軸名對照。放在 Domain，是因為權限來源記錄（AppGrant）要逐動作存。</summary>
public enum PermissionAction { Read, Add, Edit, Delete, PrintExport, Pricing, Approve }
