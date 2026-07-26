using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    public partial class AddBusinessLocations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "Services",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BusinessLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Street = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessLocations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Services_LocationId",
                table: "Services",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Services_TenantId_LocationId",
                table: "Services",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessLocations_TenantId_IsActive",
                table: "BusinessLocations",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessLocations_TenantId_IsDefault",
                table: "BusinessLocations",
                columns: new[] { "TenantId", "IsDefault" },
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_Services_BusinessLocations_LocationId",
                table: "Services",
                column: "LocationId",
                principalTable: "BusinessLocations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Preserve all existing tenants by creating one default location from
            // their current settings and assigning all legacy services to it.
            migrationBuilder.Sql(@"
                INSERT INTO BusinessLocations
                    (Id, TenantId, Name, Street, PostalCode, City, CountryCode, Currency, TimeZone, IsDefault, IsActive, CreatedAt, UpdatedAt)
                SELECT
                    NEWID(),
                    t.Id,
                    COALESCE(NULLIF(ts.CompanyName, ''), t.Name),
                    NULLIF(ts.Address, ''),
                    NULL,
                    COALESCE(NULLIF(ts.BillingCity, ''), 'Hauptstandort'),
                    COALESCE(NULLIF(ts.BillingCountry, ''), 'DE'),
                    COALESCE(NULLIF(ts.DefaultCurrency, ''), 'EUR'),
                    COALESCE(NULLIF(ts.TimeZone, ''), 'Europe/Berlin'),
                    1,
                    1,
                    GETUTCDATE(),
                    GETUTCDATE()
                FROM Tenants t
                LEFT JOIN TenantSettings ts ON ts.TenantId = t.Id
                WHERE NOT EXISTS (SELECT 1 FROM BusinessLocations l WHERE l.TenantId = t.Id);

                UPDATE s
                SET s.LocationId = l.Id,
                    s.Currency = l.Currency
                FROM Services s
                INNER JOIN BusinessLocations l ON l.TenantId = s.TenantId AND l.IsDefault = 1
                WHERE s.LocationId IS NULL;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Services_BusinessLocations_LocationId",
                table: "Services");

            migrationBuilder.DropTable(name: "BusinessLocations");
            migrationBuilder.DropIndex(name: "IX_Services_LocationId", table: "Services");
            migrationBuilder.DropIndex(name: "IX_Services_TenantId_LocationId", table: "Services");
            migrationBuilder.DropColumn(name: "LocationId", table: "Services");
        }
    }
}
