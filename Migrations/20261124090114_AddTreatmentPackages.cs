using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTreatmentPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TreatmentPackageId",
                table: "Bookings",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TreatmentPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TotalSessions = table.Column<int>(type: "int", nullable: false),
                    PreviouslyUsedSessions = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BookingToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreatmentPackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TreatmentPackages_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TreatmentPackages_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TreatmentPackages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_TreatmentPackageId",
                table: "Bookings",
                column: "TreatmentPackageId");

            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPackages_BookingToken",
                table: "TreatmentPackages",
                column: "BookingToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPackages_CustomerId",
                table: "TreatmentPackages",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPackages_ServiceId",
                table: "TreatmentPackages",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPackages_TenantId_CustomerId",
                table: "TreatmentPackages",
                columns: new[] { "TenantId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_TreatmentPackages_TenantId_Status",
                table: "TreatmentPackages",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_Bookings_TreatmentPackages_TreatmentPackageId",
                table: "Bookings",
                column: "TreatmentPackageId",
                principalTable: "TreatmentPackages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bookings_TreatmentPackages_TreatmentPackageId",
                table: "Bookings");

            migrationBuilder.DropTable(
                name: "TreatmentPackages");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_TreatmentPackageId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "TreatmentPackageId",
                table: "Bookings");
        }
    }
}
