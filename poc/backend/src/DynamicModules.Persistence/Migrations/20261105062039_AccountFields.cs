using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedLoginCount",
                table: "Dynamic_AppUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Dynamic_AppUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemoAccount",
                table: "Dynamic_AppUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntil",
                table: "Dynamic_AppUser",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "Dynamic_AppUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Dynamic_AppUser",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Dynamic_AppUser",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                defaultValue: "initial");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedLoginCount",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "IsDemoAccount",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Dynamic_AppUser");
        }
    }
}
