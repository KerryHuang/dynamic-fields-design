using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IntegrationsJson",
                table: "Dynamic_App",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Dynamic_AppBlock",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BlockId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppBlock", x => new { x.AppId, x.Name });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppBlock_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppBlock_Dynamic_Block_BlockId",
                        column: x => x.BlockId,
                        principalTable: "Dynamic_Block",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppField",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FieldId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppField", x => new { x.AppId, x.Name });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppField_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppField_Dynamic_Field_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Dynamic_Field",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppIntegration",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Installed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ItemsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppIntegration", x => new { x.AppId, x.Code });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppIntegration_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppOption",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OptionId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppOption", x => new { x.AppId, x.Name });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppOption_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppOption_Dynamic_FieldOption_OptionId",
                        column: x => x.OptionId,
                        principalTable: "Dynamic_FieldOption",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppBlock_BlockId",
                table: "Dynamic_AppBlock",
                column: "BlockId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppField_FieldId",
                table: "Dynamic_AppField",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppOption_OptionId",
                table: "Dynamic_AppOption",
                column: "OptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_AppBlock");

            migrationBuilder.DropTable(
                name: "Dynamic_AppField");

            migrationBuilder.DropTable(
                name: "Dynamic_AppIntegration");

            migrationBuilder.DropTable(
                name: "Dynamic_AppOption");

            migrationBuilder.DropColumn(
                name: "IntegrationsJson",
                table: "Dynamic_App");
        }
    }
}
