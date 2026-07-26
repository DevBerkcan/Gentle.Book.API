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
            // Guarded: the WaitlistEntries table itself (and this index) predate the migration
            // history — they were only ever created via the raw-SQL fallback in Program.cs, never
            // through a proper migration. A from-scratch replay must create the table here first.
            migrationBuilder.Sql(@"
                IF OBJECT_ID(N'WaitlistEntries', N'U') IS NULL
                BEGIN
                    CREATE TABLE WaitlistEntries (
                        Id uniqueidentifier NOT NULL DEFAULT NEWID(),
                        TenantId uniqueidentifier NOT NULL,
                        ServiceId uniqueidentifier NULL,
                        EmployeeId uniqueidentifier NULL,
                        PreferredDate date NULL,
                        FirstName nvarchar(max) NOT NULL,
                        LastName nvarchar(max) NOT NULL,
                        Email nvarchar(max) NOT NULL,
                        Phone nvarchar(max) NULL,
                        Notes nvarchar(max) NULL,
                        Status int NOT NULL CONSTRAINT DF_WaitlistEntries_Status DEFAULT 0,
                        CreatedAt datetime2 NOT NULL CONSTRAINT DF_WaitlistEntries_CreatedAt DEFAULT SYSUTCDATETIME(),
                        NotifiedAt datetime2 NULL,
                        CONSTRAINT PK_WaitlistEntries PRIMARY KEY (Id),
                        CONSTRAINT FK_WaitlistEntries_Tenants_TenantId FOREIGN KEY (TenantId)
                            REFERENCES Tenants(Id) ON DELETE CASCADE,
                        CONSTRAINT FK_WaitlistEntries_Services_ServiceId FOREIGN KEY (ServiceId)
                            REFERENCES Services(Id),
                        CONSTRAINT FK_WaitlistEntries_Employees_EmployeeId FOREIGN KEY (EmployeeId)
                            REFERENCES Employees(Id)
                    );
                END
                ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WaitlistEntries_TenantId' AND object_id = OBJECT_ID('WaitlistEntries'))
                    DROP INDEX IX_WaitlistEntries_TenantId ON WaitlistEntries;
            ");

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
