using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedMailSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_MailSettings",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Security = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: true),
                    FromAddress = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    FromName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_MailSettings", x => x.Kind);
                });

            // 手寫（Up 唯一的一段；Down 的一段見下方）：o2c 告警通道列的 SMTP 連線欄位原樣搬到核心寄信設定那一列（使用者 2026-11-19 裁決，帳號信與告警信共用連線）。
            // 必須在建表之後、刪欄位之前，所以 EF 產生的 CreateTable 移到 DropColumn 前面（操作本身沒改）。
            // 密碼密文原樣搬：Data Protection 的 purpose 維持 ops-alert-smtp，搬過去的密文才解得開。沒有通道列（從沒設定過）就不建列。
            // SQLite：識別字照樣加雙引號（與 PostgreSQL 版同一句）。
            migrationBuilder.Sql(
                "INSERT INTO \"Dynamic_MailSettings\" (\"Kind\", \"Host\", \"Port\", \"Security\", \"UserName\", \"PasswordProtected\", " +
                "\"FromAddress\", \"FromName\", \"ModifiedUserId\", \"ModifiedUtc\") " +
                "SELECT 'default', \"Host\", \"Port\", \"Security\", \"UserName\", \"PasswordProtected\", \"FromAddress\", \"FromName\", " +
                "\"ModifiedUserId\", \"ModifiedUtc\" FROM \"Dynamic_AlertChannel\" WHERE \"Kind\" = 'smtp';");

            migrationBuilder.DropColumn(
                name: "FromAddress",
                table: "Dynamic_AlertChannel");

            migrationBuilder.DropColumn(
                name: "FromName",
                table: "Dynamic_AlertChannel");

            migrationBuilder.DropColumn(
                name: "Host",
                table: "Dynamic_AlertChannel");

            migrationBuilder.DropColumn(
                name: "PasswordProtected",
                table: "Dynamic_AlertChannel");

            migrationBuilder.DropColumn(
                name: "Port",
                table: "Dynamic_AlertChannel");

            migrationBuilder.DropColumn(
                name: "Security",
                table: "Dynamic_AlertChannel");

            migrationBuilder.DropColumn(
                name: "UserName",
                table: "Dynamic_AlertChannel");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FromAddress",
                table: "Dynamic_AlertChannel",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FromName",
                table: "Dynamic_AlertChannel",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Host",
                table: "Dynamic_AlertChannel",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PasswordProtected",
                table: "Dynamic_AlertChannel",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Port",
                table: "Dynamic_AlertChannel",
                type: "INTEGER",
                nullable: false,
                defaultValue: 587);

            migrationBuilder.AddColumn<string>(
                name: "Security",
                table: "Dynamic_AlertChannel",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "startTls");

            migrationBuilder.AddColumn<string>(
                name: "UserName",
                table: "Dynamic_AlertChannel",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            // 手寫（審查修正 2）：Down 不能只還原結構——舊模型以 Enum.Parse 讀 Security，EF 產生的預設值（Security=""、Port=0）會讓舊版讀取即拋例外，
            // 再 Up 又把非法值搬進寄信設定。所以 AddColumn 的預設值改成舊模型的出廠值（587、startTls），寄信設定那一列反向搬回通道列，最後才刪表。
            // 寄信設定有、通道列沒有（新版只設了寄信、沒開過告警通道）時，舊結構沒有地方放，連線隨表刪除（只影響降版）。兩種資料庫同一句。
            migrationBuilder.Sql(
                "UPDATE \"Dynamic_AlertChannel\" SET " +
                "\"Host\" = (SELECT m.\"Host\" FROM \"Dynamic_MailSettings\" m WHERE m.\"Kind\" = 'default'), " +
                "\"Port\" = (SELECT m.\"Port\" FROM \"Dynamic_MailSettings\" m WHERE m.\"Kind\" = 'default'), " +
                "\"Security\" = (SELECT m.\"Security\" FROM \"Dynamic_MailSettings\" m WHERE m.\"Kind\" = 'default'), " +
                "\"UserName\" = (SELECT m.\"UserName\" FROM \"Dynamic_MailSettings\" m WHERE m.\"Kind\" = 'default'), " +
                "\"PasswordProtected\" = (SELECT m.\"PasswordProtected\" FROM \"Dynamic_MailSettings\" m WHERE m.\"Kind\" = 'default'), " +
                "\"FromAddress\" = (SELECT m.\"FromAddress\" FROM \"Dynamic_MailSettings\" m WHERE m.\"Kind\" = 'default'), " +
                "\"FromName\" = (SELECT m.\"FromName\" FROM \"Dynamic_MailSettings\" m WHERE m.\"Kind\" = 'default') " +
                "WHERE \"Kind\" = 'smtp' AND EXISTS (SELECT 1 FROM \"Dynamic_MailSettings\" m WHERE m.\"Kind\" = 'default');");

            migrationBuilder.DropTable(
                name: "Dynamic_MailSettings");
        }
    }
}
