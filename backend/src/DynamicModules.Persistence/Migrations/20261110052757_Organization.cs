using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Organization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_Department",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NameI18n = table.Column<string>(type: "TEXT", nullable: false),
                    ParentId = table.Column<long>(type: "INTEGER", nullable: true),
                    ManagerUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Department", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_UserDepartment",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DepartmentId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_UserDepartment", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_Dynamic_UserDepartment_Dynamic_Department_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Dynamic_Department",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Department_Code",
                table: "Dynamic_Department",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Department_ParentId",
                table: "Dynamic_Department",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_UserDepartment_DepartmentId",
                table: "Dynamic_UserDepartment",
                column: "DepartmentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_UserDepartment");

            migrationBuilder.DropTable(
                name: "Dynamic_Department");
        }
    }
}
