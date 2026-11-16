using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_AppUser",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppUser", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Module",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ProgramCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    NameI18n = table.Column<string>(type: "TEXT", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Section = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TitleFieldId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Module", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Program",
                columns: table => new
                {
                    ProgramCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    NameI18n = table.Column<string>(type: "TEXT", nullable: false),
                    IsBeta = table.Column<bool>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Program", x => x.ProgramCode);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Block",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    LabelI18n = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Collapsed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Block", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Block_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Record",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DeleteFlag = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Record", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Record_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Field",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    BlockId = table.Column<long>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LabelI18n = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Config = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Field", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Field_Dynamic_Block_BlockId",
                        column: x => x.BlockId,
                        principalTable: "Dynamic_Block",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_Field_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_FieldOption",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FieldId = table.Column<long>(type: "INTEGER", nullable: false),
                    LabelI18n = table.Column<string>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_FieldOption", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_FieldOption_Dynamic_Field_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Dynamic_Field",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Block_ModuleId",
                table: "Dynamic_Block",
                column: "ModuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Field_BlockId",
                table: "Dynamic_Field",
                column: "BlockId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Field_ModuleId_Key",
                table: "Dynamic_Field",
                columns: new[] { "ModuleId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_FieldOption_FieldId",
                table: "Dynamic_FieldOption",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Module_Key",
                table: "Dynamic_Module",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Record_ModuleId_DeleteFlag",
                table: "Dynamic_Record",
                columns: new[] { "ModuleId", "DeleteFlag" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_AppUser");

            migrationBuilder.DropTable(
                name: "Dynamic_FieldOption");

            migrationBuilder.DropTable(
                name: "Dynamic_Program");

            migrationBuilder.DropTable(
                name: "Dynamic_Record");

            migrationBuilder.DropTable(
                name: "Dynamic_Field");

            migrationBuilder.DropTable(
                name: "Dynamic_Block");

            migrationBuilder.DropTable(
                name: "Dynamic_Module");
        }
    }
}
