using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixMissingSubscriptionRequestsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EmailLogs_Tenants_TenantId",
                table: "EmailLogs");

            migrationBuilder.AddForeignKey(
                name: "FK_EmailLogs_Tenants_TenantId",
                table: "EmailLogs",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id");

            // Guarded: SubscriptionRequests has no CreateTable in any prior migration and no
            // Program.cs raw-SQL fallback either — on environments where InitialGentleBookSchema
            // was already recorded as applied before this table was added, it simply never got
            // created. The Mollie mandate flow reads/writes this table, so create it here.
            migrationBuilder.Sql(@"
                IF OBJECT_ID(N'SubscriptionRequests', N'U') IS NULL
                BEGIN
                    CREATE TABLE SubscriptionRequests (
                        Id uniqueidentifier NOT NULL DEFAULT NEWID(),
                        TenantId uniqueidentifier NOT NULL,
                        RequestedPlan nvarchar(50) NOT NULL,
                        ContactEmail nvarchar(200) NOT NULL,
                        Note nvarchar(500) NULL,
                        Status nvarchar(20) NOT NULL CONSTRAINT DF_SubscriptionRequests_Status DEFAULT 'Pending',
                        CreatedAt datetime2 NOT NULL CONSTRAINT DF_SubscriptionRequests_CreatedAt DEFAULT SYSUTCDATETIME(),
                        ProcessedAt datetime2 NULL,
                        CONSTRAINT PK_SubscriptionRequests PRIMARY KEY (Id),
                        CONSTRAINT FK_SubscriptionRequests_Tenants_TenantId FOREIGN KEY (TenantId)
                            REFERENCES Tenants(Id) ON DELETE CASCADE
                    );
                    CREATE INDEX IX_SubscriptionRequests_TenantId_Status ON SubscriptionRequests(TenantId, Status);
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EmailLogs_Tenants_TenantId",
                table: "EmailLogs");

            migrationBuilder.AddForeignKey(
                name: "FK_EmailLogs_Tenants_TenantId",
                table: "EmailLogs",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
