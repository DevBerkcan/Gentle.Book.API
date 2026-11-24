using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStudioPassAndReferrals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReferralEnabled",
                table: "TenantSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReferralRewardText",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PassToken",
                table: "Customers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferralCode",
                table: "Customers",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Referrals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferrerCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferredCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstBookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QualifyingBookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    QualifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RewardText = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ReferrerRewardRedeemedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReferredRewardRedeemedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Referrals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Referrals_Customers_ReferredCustomerId",
                        column: x => x.ReferredCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Referrals_Customers_ReferrerCustomerId",
                        column: x => x.ReferrerCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Referrals_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_PassToken",
                table: "Customers",
                column: "PassToken",
                unique: true,
                filter: "[PassToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_ReferralCode",
                table: "Customers",
                column: "ReferralCode",
                unique: true,
                filter: "[ReferralCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferredCustomerId",
                table: "Referrals",
                column: "ReferredCustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferrerCustomerId",
                table: "Referrals",
                column: "ReferrerCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_TenantId_Status",
                table: "Referrals",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Referrals");

            migrationBuilder.DropIndex(
                name: "IX_Customers_PassToken",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_ReferralCode",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ReferralEnabled",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "ReferralRewardText",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "PassToken",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ReferralCode",
                table: "Customers");
        }
    }
}
