using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// migration SharedMailSettings（帳號信與告警信共用寄信設定，使用者 2026-11-19 裁決）：o2c 告警通道列的 SMTP 連線欄位搬到
/// <c>Dynamic_MailSettings</c> 那一列（密碼密文原樣），通道列只剩自己的欄位。兩種資料庫都跑（資料搬移是 migration 裡唯一的手寫 SQL）。
/// </summary>
[Trait("Category", "Postgres")]
public class MailSettingsMigrationTests
{
    private const string Previous = "OpsEventCenter";

    [Fact]
    public async Task ExistingSmtpChannel_ConnectionMovedToMailSettings_CiphertextUnchanged_ChannelKeepsOwnColumns()
    {
        await using var db = await TestDb.CreateAsync(migrate: false);
        var migrator = db.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous);
        var modified = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc);
        var success = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        const string cipher = "CfDJ8-fake-ciphertext-kept-as-is";
        await db.Context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Dynamic_AlertChannel\" (\"Kind\", \"Enabled\", \"Host\", \"Port\", \"Security\", \"UserName\", \"PasswordProtected\", " +
            "\"FromAddress\", \"FromName\", \"ToAddresses\", \"MailLocale\", \"LastSuccessUtc\", \"ConsecutiveFailures\", \"ModifiedUserId\", \"ModifiedUtc\") " +
            "VALUES ('smtp', {0}, 'smtp.test.invalid', 465, 'sslOnConnect', 'ops', {1}, 'ops@test.invalid', 'Formwright', " +
            "'a@test.invalid,b@test.invalid', 'en-US', {2}, 2, 'admin', {3})",
            true, cipher, success, modified);

        await migrator.MigrateAsync();

        var mail = await db.Context.Set<MailSettings>().AsNoTracking().SingleAsync();
        mail.Kind.Should().Be(MailSettings.Default);
        mail.Host.Should().Be("smtp.test.invalid");
        mail.Port.Should().Be(465);
        mail.Security.Should().Be(AlertSecurity.SslOnConnect);
        mail.UserName.Should().Be("ops");
        mail.PasswordProtected.Should().Be(cipher, "密文原樣搬（purpose 不變才解得開）");
        mail.FromAddress.Should().Be("ops@test.invalid");
        mail.FromName.Should().Be("Formwright");
        mail.ModifiedUserId.Should().Be("admin");
        mail.ModifiedUtc.Should().Be(modified);

        var channel = await db.Context.Set<AlertChannel>().AsNoTracking().SingleAsync();
        channel.Enabled.Should().BeTrue();
        channel.ToAddresses.Should().Be("a@test.invalid,b@test.invalid");
        channel.MailLocale.Should().Be("en-US");
        channel.LastSuccessUtc.Should().Be(success);
        channel.ConsecutiveFailures.Should().Be(2);
        (await ColumnsAsync(db, "Dynamic_AlertChannel")).Should().BeEquivalentTo(
            "Kind", "Enabled", "ToAddresses", "MailLocale", "LastClaimUtc", "LastSuccessUtc", "LastFailureUtc", "LastError",
            "ConsecutiveFailures", "ModifiedUserId", "ModifiedUtc");
    }

    [Fact]
    public async Task NoChannelRow_NoMailSettingsRow()
    {
        await using var db = await TestDb.CreateAsync(migrate: false);
        var migrator = db.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous);
        await migrator.MigrateAsync();

        (await db.Context.Set<MailSettings>().AsNoTracking().AnyAsync()).Should().BeFalse("從沒設定過通道就不建寄信設定列");
    }

    private static Task<List<string>> ColumnsAsync(TestDb db, string table) => db.Context.Database.SqlQueryRaw<string>(
        TestDatabase.IsPostgres
            ? "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = {0}"
            : "SELECT name AS \"Value\" FROM pragma_table_info({0})",
        table).ToListAsync();
}
