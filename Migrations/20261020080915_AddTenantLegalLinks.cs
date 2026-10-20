using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantLegalLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImprintUrl",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrivacyUrl",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImprintUrl",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "PrivacyUrl",
                table: "TenantSettings");
        }
    }
}
