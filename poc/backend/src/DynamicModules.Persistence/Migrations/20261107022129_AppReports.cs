using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_AppReport",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReportId = table.Column<long>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppReport", x => new { x.AppId, x.ReportId });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppReport_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppReport_Dynamic_Report_ReportId",
                        column: x => x.ReportId,
                        principalTable: "Dynamic_Report",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppReport_AppId_Code",
                table: "Dynamic_AppReport",
                columns: new[] { "AppId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppReport_ReportId",
                table: "Dynamic_AppReport",
                column: "ReportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_AppReport");
        }
    }
}
