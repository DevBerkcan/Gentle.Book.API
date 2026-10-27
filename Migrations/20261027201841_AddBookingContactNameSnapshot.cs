using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingContactNameSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactFirstName",
                table: "Bookings",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactLastName",
                table: "Bookings",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContactFirstName",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ContactLastName",
                table: "Bookings");
        }
    }
}
