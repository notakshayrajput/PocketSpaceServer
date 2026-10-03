using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PocketSpaceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndexedFileCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_UserId_RecentAt",
                table: "Files");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Files",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "IsPresent",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastModified",
                table: "Files",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Files",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NameSortKey",
                table: "Files",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OrdinalNameSortKey",
                table: "Files",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ParentPathKey",
                table: "Files",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "Size",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_Files_Backend_UserId_ParentPathKey_IsPresent_IsFolder_CreatedAt",
                table: "Files",
                columns: new[] { "Backend", "UserId", "ParentPathKey", "IsPresent", "IsFolder", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Backend_UserId_ParentPathKey_IsPresent_IsFolder_LastModified",
                table: "Files",
                columns: new[] { "Backend", "UserId", "ParentPathKey", "IsPresent", "IsFolder", "LastModified" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Backend_UserId_ParentPathKey_IsPresent_IsFolder_NameSortKey",
                table: "Files",
                columns: new[] { "Backend", "UserId", "ParentPathKey", "IsPresent", "IsFolder", "NameSortKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Backend_UserId_ParentPathKey_IsPresent_IsFolder_Size",
                table: "Files",
                columns: new[] { "Backend", "UserId", "ParentPathKey", "IsPresent", "IsFolder", "Size" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_UserId_Backend_IsPresent_IsFavorite",
                table: "Files",
                columns: new[] { "UserId", "Backend", "IsPresent", "IsFavorite" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_UserId_Backend_IsPresent_RecentAt",
                table: "Files",
                columns: new[] { "UserId", "Backend", "IsPresent", "RecentAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_Backend_UserId_ParentPathKey_IsPresent_IsFolder_CreatedAt",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_Backend_UserId_ParentPathKey_IsPresent_IsFolder_LastModified",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_Backend_UserId_ParentPathKey_IsPresent_IsFolder_NameSortKey",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_Backend_UserId_ParentPathKey_IsPresent_IsFolder_Size",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_UserId_Backend_IsPresent_IsFavorite",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_UserId_Backend_IsPresent_RecentAt",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "IsPresent",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "LastModified",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "NameSortKey",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "OrdinalNameSortKey",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "ParentPathKey",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "Size",
                table: "Files");

            migrationBuilder.CreateIndex(
                name: "IX_Files_UserId_RecentAt",
                table: "Files",
                columns: new[] { "UserId", "RecentAt" });
        }
    }
}
