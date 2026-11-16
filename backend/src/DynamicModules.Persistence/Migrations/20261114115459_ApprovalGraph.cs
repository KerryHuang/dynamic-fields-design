using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DataJson",
                table: "Dynamic_Notification",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloseReason",
                table: "Dynamic_ApprovalStageRun",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InEdgeId",
                table: "Dynamic_ApprovalStageRun",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NodeKind",
                table: "Dynamic_ApprovalStageRun",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LayoutJson",
                table: "Dynamic_ApprovalFlowVersion",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftJson",
                table: "Dynamic_ApprovalFlow",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftUpdatedBy",
                table: "Dynamic_ApprovalFlow",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DraftUpdatedTime",
                table: "Dynamic_ApprovalFlow",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataJson",
                table: "Dynamic_Notification");

            migrationBuilder.DropColumn(
                name: "CloseReason",
                table: "Dynamic_ApprovalStageRun");

            migrationBuilder.DropColumn(
                name: "InEdgeId",
                table: "Dynamic_ApprovalStageRun");

            migrationBuilder.DropColumn(
                name: "NodeKind",
                table: "Dynamic_ApprovalStageRun");

            migrationBuilder.DropColumn(
                name: "LayoutJson",
                table: "Dynamic_ApprovalFlowVersion");

            migrationBuilder.DropColumn(
                name: "DraftJson",
                table: "Dynamic_ApprovalFlow");

            migrationBuilder.DropColumn(
                name: "DraftUpdatedBy",
                table: "Dynamic_ApprovalFlow");

            migrationBuilder.DropColumn(
                name: "DraftUpdatedTime",
                table: "Dynamic_ApprovalFlow");
        }
    }
}
