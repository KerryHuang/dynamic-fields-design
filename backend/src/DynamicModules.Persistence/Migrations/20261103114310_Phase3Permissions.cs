using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Permissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystemAdmin",
                table: "Dynamic_AppUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Dynamic_FieldOptionScope",
                columns: table => new
                {
                    OptionId = table.Column<long>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_FieldOptionScope", x => new { x.OptionId, x.GroupId });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_FieldPermission",
                columns: table => new
                {
                    FieldId = table.Column<long>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    Access = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_FieldPermission", x => new { x.FieldId, x.GroupId });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_GroupProgramPrivilege",
                columns: table => new
                {
                    ProgramCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    GroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    CanRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanAdd = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanEdit = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanDelete = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanPrint = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanAmt = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanApprove = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_GroupProgramPrivilege", x => new { x.GroupId, x.ProgramCode });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ModuleDataScope",
                columns: table => new
                {
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ModuleDataScope", x => new { x.ModuleId, x.GroupId });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_PermissionGroup",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NameI18n = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_PermissionGroup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_UserGroupMapping",
                columns: table => new
                {
                    GroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_UserGroupMapping", x => new { x.GroupId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_UserProgramPrivilege",
                columns: table => new
                {
                    ProgramCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CanRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanAdd = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanEdit = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanDelete = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanPrint = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanAmt = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanApprove = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_UserProgramPrivilege", x => new { x.UserId, x.ProgramCode });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_PermissionGroup_Key",
                table: "Dynamic_PermissionGroup",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_FieldOptionScope");

            migrationBuilder.DropTable(
                name: "Dynamic_FieldPermission");

            migrationBuilder.DropTable(
                name: "Dynamic_GroupProgramPrivilege");

            migrationBuilder.DropTable(
                name: "Dynamic_ModuleDataScope");

            migrationBuilder.DropTable(
                name: "Dynamic_PermissionGroup");

            migrationBuilder.DropTable(
                name: "Dynamic_UserGroupMapping");

            migrationBuilder.DropTable(
                name: "Dynamic_UserProgramPrivilege");

            migrationBuilder.DropColumn(
                name: "IsSystemAdmin",
                table: "Dynamic_AppUser");
        }
    }
}
