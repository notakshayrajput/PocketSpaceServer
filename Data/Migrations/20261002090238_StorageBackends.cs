using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PocketSpaceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class StorageBackends : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_UserId_PathKey",
                table: "Files");

            migrationBuilder.AddColumn<string>(
                name: "Backend",
                table: "TrashEntries",
                type: "TEXT",
                nullable: false,
                defaultValue: "FileSystem");

            migrationBuilder.AddColumn<string>(
                name: "Backend",
                table: "Files",
                type: "TEXT",
                nullable: false,
                defaultValue: "FileSystem");

            migrationBuilder.CreateTable(
                name: "StorageConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Backend = table.Column<string>(type: "TEXT", nullable: false),
                    GlobalLimitBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Bucket = table.Column<string>(type: "TEXT", nullable: true),
                    Region = table.Column<string>(type: "TEXT", nullable: true),
                    AccessKeyCiphertext = table.Column<string>(type: "TEXT", nullable: true),
                    SecretKeyCiphertext = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageConfigurations", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "StorageConfigurations",
                columns: new[] { "Id", "AccessKeyCiphertext", "Backend", "Bucket", "GlobalLimitBytes", "Region", "SecretKeyCiphertext" },
                values: new object[] { 1, null, "FileSystem", null, null, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Backend_UserId_PathKey",
                table: "Files",
                columns: new[] { "Backend", "UserId", "PathKey" },
                unique: true,
                filter: "\"TrashEntryId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_Files_Backend_UserId_PathKey",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "Backend",
                table: "TrashEntries");

            migrationBuilder.DropColumn(
                name: "Backend",
                table: "Files");

            migrationBuilder.CreateIndex(
                name: "IX_Files_UserId_PathKey",
                table: "Files",
                columns: new[] { "UserId", "PathKey" },
                unique: true,
                filter: "\"TrashEntryId\" IS NULL");
        }
    }
}
