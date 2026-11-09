using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedMasters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VisibilityRestricted",
                table: "Dynamic_Field",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Dynamic_App",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "App");

            migrationBuilder.CreateTable(
                name: "Dynamic_AppGrant",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    AppId = table.Column<long>(type: "INTEGER", nullable: true),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppGrant", x => x.Id);
                    table.CheckConstraint("CK_AppGrant_Source", "(\"SourceKind\" = 'Manual') = (\"AppId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Dynamic_AppGrant_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppGrant_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppGrant_Dynamic_PermissionGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Dynamic_PermissionGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppMasterUse",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "INTEGER", nullable: false),
                    MasterAppId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppMasterUse", x => new { x.AppId, x.MasterAppId });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppMasterUse_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppMasterUse_Dynamic_App_MasterAppId",
                        column: x => x.MasterAppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppOrphan",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FormerAppKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FieldId = table.Column<long>(type: "INTEGER", nullable: true),
                    OptionId = table.Column<long>(type: "INTEGER", nullable: true),
                    BlockId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppOrphan", x => x.Id);
                    table.CheckConstraint("CK_AppOrphan_One", "(\"FieldId\" IS NOT NULL) + (\"OptionId\" IS NOT NULL) + (\"BlockId\" IS NOT NULL) = 1");
                    table.ForeignKey(
                        name: "FK_Dynamic_AppOrphan_Dynamic_Block_BlockId",
                        column: x => x.BlockId,
                        principalTable: "Dynamic_Block",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppOrphan_Dynamic_FieldOption_OptionId",
                        column: x => x.OptionId,
                        principalTable: "Dynamic_FieldOption",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppOrphan_Dynamic_Field_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Dynamic_Field",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_FieldGrant",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    FieldId = table.Column<long>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    AppId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_FieldGrant", x => x.Id);
                    table.CheckConstraint("CK_FieldGrant_Source", "(\"SourceKind\" = 'Manual') = (\"AppId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Dynamic_FieldGrant_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_FieldGrant_Dynamic_Field_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Dynamic_Field",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_FieldGrant_Dynamic_PermissionGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Dynamic_PermissionGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ModuleActionScope",
                columns: table => new
                {
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ModuleActionScope", x => new { x.ModuleId, x.GroupId, x.Action });
                    table.ForeignKey(
                        name: "FK_Dynamic_ModuleActionScope_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_ModuleActionScope_Dynamic_PermissionGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Dynamic_PermissionGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppGrant_AppId_ModuleId_GroupId_Action",
                table: "Dynamic_AppGrant",
                columns: new[] { "AppId", "ModuleId", "GroupId", "Action" },
                unique: true,
                filter: "\"AppId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppGrant_GroupId",
                table: "Dynamic_AppGrant",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppGrant_ModuleId_GroupId_Action",
                table: "Dynamic_AppGrant",
                columns: new[] { "ModuleId", "GroupId", "Action" },
                unique: true,
                filter: "\"AppId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppMasterUse_MasterAppId",
                table: "Dynamic_AppMasterUse",
                column: "MasterAppId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppOrphan_BlockId",
                table: "Dynamic_AppOrphan",
                column: "BlockId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppOrphan_FieldId",
                table: "Dynamic_AppOrphan",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppOrphan_FormerAppKey_Kind_Name",
                table: "Dynamic_AppOrphan",
                columns: new[] { "FormerAppKey", "Kind", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppOrphan_OptionId",
                table: "Dynamic_AppOrphan",
                column: "OptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_FieldGrant_AppId",
                table: "Dynamic_FieldGrant",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_FieldGrant_FieldId_GroupId",
                table: "Dynamic_FieldGrant",
                columns: new[] { "FieldId", "GroupId" },
                unique: true,
                filter: "\"AppId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_FieldGrant_FieldId_GroupId_AppId",
                table: "Dynamic_FieldGrant",
                columns: new[] { "FieldId", "GroupId", "AppId" },
                unique: true,
                filter: "\"AppId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_FieldGrant_GroupId",
                table: "Dynamic_FieldGrant",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ModuleActionScope_GroupId",
                table: "Dynamic_ModuleActionScope",
                column: "GroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_AppGrant");

            migrationBuilder.DropTable(
                name: "Dynamic_AppMasterUse");

            migrationBuilder.DropTable(
                name: "Dynamic_AppOrphan");

            migrationBuilder.DropTable(
                name: "Dynamic_FieldGrant");

            migrationBuilder.DropTable(
                name: "Dynamic_ModuleActionScope");

            migrationBuilder.DropColumn(
                name: "VisibilityRestricted",
                table: "Dynamic_Field");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Dynamic_App");
        }
    }
}
