using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PocketSpaceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class StorageConfigurationFromEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessKeyCiphertext",
                table: "StorageConfigurations");

            migrationBuilder.DropColumn(
                name: "Backend",
                table: "StorageConfigurations");

            migrationBuilder.DropColumn(
                name: "Bucket",
                table: "StorageConfigurations");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "StorageConfigurations");

            migrationBuilder.DropColumn(
                name: "SecretKeyCiphertext",
                table: "StorageConfigurations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessKeyCiphertext",
                table: "StorageConfigurations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Backend",
                table: "StorageConfigurations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Bucket",
                table: "StorageConfigurations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "StorageConfigurations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretKeyCiphertext",
                table: "StorageConfigurations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "StorageConfigurations",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AccessKeyCiphertext", "Backend", "Bucket", "Region", "SecretKeyCiphertext" },
                values: new object[] { null, "FileSystem", null, null, null });
        }
    }
}
