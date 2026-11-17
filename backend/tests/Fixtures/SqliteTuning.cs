using System.Runtime.CompilerServices;
using SQLitePCL;

namespace DynamicModules.TestSupport;

/// <summary>
/// 測試行程啟動時關掉 SQLite 的記憶體統計（SQLITE_CONFIG_MEMSTATUS）。開著時每次 sqlite3Malloc／sqlite3_free 都搶同一把全域 mutex，
/// 多個 Demo 產生（每次約 11.8 萬條 SQL）同時跑時，執行緒幾乎都卡在這把鎖上。只影響測試行程，產品不變。
/// </summary>
internal static class SqliteTuning
{
    private const int ConfigMemStatus = 9;

    [ModuleInitializer]
    internal static void Init()
    {
        Batteries_V2.Init();
        raw.sqlite3_config(ConfigMemStatus, 0);
    }
}
