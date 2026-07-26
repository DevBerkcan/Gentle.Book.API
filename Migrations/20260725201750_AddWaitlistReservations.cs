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

            // Guarded — same out-of-band fallback drift as everything else in this migration.
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'PreferredStartTime')
                    ALTER TABLE WaitlistEntries ADD PreferredStartTime time NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'PreferredEndTime')
                    ALTER TABLE WaitlistEntries ADD PreferredEndTime time NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'ReservationToken')
                    ALTER TABLE WaitlistEntries ADD ReservationToken nvarchar(128) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'ReservationExpiresAt')
                    ALTER TABLE WaitlistEntries ADD ReservationExpiresAt datetime2 NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'ReservedStartTime')
                    ALTER TABLE WaitlistEntries ADD ReservedStartTime time NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'ReservedEndTime')
                    ALTER TABLE WaitlistEntries ADD ReservedEndTime time NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'ReservedEmployeeId')
                    ALTER TABLE WaitlistEntries ADD ReservedEmployeeId uniqueidentifier NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'BookingId')
                    ALTER TABLE WaitlistEntries ADD BookingId uniqueidentifier NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'BookedAt')
                    ALTER TABLE WaitlistEntries ADD BookedAt datetime2 NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WaitlistEntries_ReservationToken' AND object_id = OBJECT_ID('WaitlistEntries'))
                    CREATE INDEX IX_WaitlistEntries_ReservationToken ON WaitlistEntries(ReservationToken);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WaitlistEntries_TenantId_Status_PreferredDate' AND object_id = OBJECT_ID('WaitlistEntries'))
                    CREATE INDEX IX_WaitlistEntries_TenantId_Status_PreferredDate ON WaitlistEntries(TenantId, Status, PreferredDate);
            ");
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
