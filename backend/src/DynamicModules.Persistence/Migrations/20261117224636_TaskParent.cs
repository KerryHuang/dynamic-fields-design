using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelledByParent",
                table: "Dynamic_Task",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ParentTaskId",
                table: "Dynamic_Task",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Task_ParentTaskId",
                table: "Dynamic_Task",
                column: "ParentTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Dynamic_Task_ParentTaskId",
                table: "Dynamic_Task");

            migrationBuilder.DropColumn(
                name: "CancelledByParent",
                table: "Dynamic_Task");

            migrationBuilder.DropColumn(
                name: "ParentTaskId",
                table: "Dynamic_Task");
        }
    }
}
