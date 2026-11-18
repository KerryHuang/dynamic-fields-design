using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.Conventions;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// 拿掉 Npgsql 把 <c>JsonElement</c> 屬性一律改成 <c>jsonb</c> 的慣例：那會無視既有的值轉換（存原始 JSON 文字），
/// 而 jsonb 會重排鍵與空白，讀回的文字與 SQLite 不同。JSON 欄位維持 text（PostgreSQL provider 設計 §5.4）。
/// </summary>
#pragma warning disable EF1001 // NpgsqlConventionSetBuilder 的建構子參數是 Npgsql 的內部型別；EF 升級時由 PostgresModelTests 把關
public sealed class PostgresConventionSetBuilder(
    ProviderConventionSetBuilderDependencies dependencies,
    RelationalConventionSetBuilderDependencies relationalDependencies,
    Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource typeMappingSource,
    INpgsqlSingletonOptions npgsqlSingletonOptions)
    : NpgsqlConventionSetBuilder(dependencies, relationalDependencies, typeMappingSource, npgsqlSingletonOptions)
#pragma warning restore EF1001
{
    public override ConventionSet CreateConventionSet()
    {
        var conventions = base.CreateConventionSet();
        conventions.Remove(typeof(NpgsqlJsonElementHackConvention));
        return conventions;
    }
}
