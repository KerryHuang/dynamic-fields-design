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

            // 手寫（唯一的一段）：o2c 告警通道列的 SMTP 連線欄位原樣搬到核心寄信設定那一列（使用者 2026-11-19 裁決，帳號信與告警信共用連線）。
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
            migrationBuilder.DropTable(
                name: "Dynamic_MailSettings");

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
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Security",
                table: "Dynamic_AlertChannel",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UserName",
                table: "Dynamic_AlertChannel",
                type: "TEXT",
                maxLength: 255,
                nullable: true);
        }
    }
}
