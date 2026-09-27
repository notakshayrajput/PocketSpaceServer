using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PocketSpaceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "QuotaBytes",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 524288000L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuotaBytes",
                table: "AspNetUsers");
        }
    }
}
