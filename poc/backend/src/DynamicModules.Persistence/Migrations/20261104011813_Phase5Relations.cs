using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5Relations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HostKey",
                table: "Dynamic_Record",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostEntityKey",
                table: "Dynamic_Module",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Dynamic_Module",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Standalone");

            migrationBuilder.CreateTable(
                name: "Mock_Customer",
                columns: table => new
                {
                    CustomerNo = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DeleteFlag = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mock_Customer", x => x.CustomerNo);
                });

            migrationBuilder.CreateTable(
                name: "Mock_Mold",
                columns: table => new
                {
                    MoldNo = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DeleteFlag = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mock_Mold", x => x.MoldNo);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Record_ModuleId_HostKey",
                table: "Dynamic_Record",
                columns: new[] { "ModuleId", "HostKey" },
                unique: true,
                filter: "\"HostKey\" IS NOT NULL AND \"DeleteFlag\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Module_HostEntityKey",
                table: "Dynamic_Module",
                column: "HostEntityKey",
                unique: true,
                filter: "\"HostEntityKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Mock_Customer");

            migrationBuilder.DropTable(
                name: "Mock_Mold");

            migrationBuilder.DropIndex(
                name: "IX_Dynamic_Record_ModuleId_HostKey",
                table: "Dynamic_Record");

            migrationBuilder.DropIndex(
                name: "IX_Dynamic_Module_HostEntityKey",
                table: "Dynamic_Module");

            migrationBuilder.DropColumn(
                name: "HostKey",
                table: "Dynamic_Record");

            migrationBuilder.DropColumn(
                name: "HostEntityKey",
                table: "Dynamic_Module");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Dynamic_Module");
        }
    }
}
