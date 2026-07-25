using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitlistReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WaitlistEntries_TenantId",
                table: "WaitlistEntries");

            migrationBuilder.AddColumn<DateTime>(
                name: "BookedAt",
                table: "WaitlistEntries",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BookingId",
                table: "WaitlistEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "PreferredEndTime",
                table: "WaitlistEntries",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "PreferredStartTime",
                table: "WaitlistEntries",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReservationExpiresAt",
                table: "WaitlistEntries",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReservationToken",
                table: "WaitlistEntries",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReservedEmployeeId",
                table: "WaitlistEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ReservedEndTime",
                table: "WaitlistEntries",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ReservedStartTime",
                table: "WaitlistEntries",
                type: "time",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_ReservationToken",
                table: "WaitlistEntries",
                column: "ReservationToken");

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_TenantId_Status_PreferredDate",
                table: "WaitlistEntries",
                columns: new[] { "TenantId", "Status", "PreferredDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WaitlistEntries_ReservationToken",
                table: "WaitlistEntries");

            migrationBuilder.DropIndex(
                name: "IX_WaitlistEntries_TenantId_Status_PreferredDate",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "BookedAt",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "BookingId",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "PreferredEndTime",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "PreferredStartTime",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "ReservationExpiresAt",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "ReservationToken",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "ReservedEmployeeId",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "ReservedEndTime",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "ReservedStartTime",
                table: "WaitlistEntries");

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_TenantId",
                table: "WaitlistEntries",
                column: "TenantId");
        }
    }
}
