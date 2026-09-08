using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantAiBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantAiBudgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MonthlyBudgetUsd = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    CurrentPeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CurrentPeriodCostUsd = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                    IsHardCapped = table.Column<bool>(type: "bit", nullable: false),
                    HardCapWarningEmailSent = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAiBudgets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantAiBudgets_TenantId",
                table: "TenantAiBudgets",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantAiBudgets");
        }
    }
}
