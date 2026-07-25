using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMollieAndBillingSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "StripeSubscriptionId",
                table: "Subscriptions",
                newName: "MollieMandateId");

            migrationBuilder.RenameColumn(
                name: "StripeCustomerId",
                table: "Subscriptions",
                newName: "CrmCustomerId");

            migrationBuilder.AddColumn<string>(
                name: "BillingCity",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingCountry",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingStreet",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingZipCode",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalCompanyName",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatId",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastMolliePaymentId",
                table: "Subscriptions",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MollieCustomerId",
                table: "Subscriptions",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MollieMandateSignedAt",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MollieSubscriptionId",
                table: "Subscriptions",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MollieWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MollieResourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResultStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MollieWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_LastMolliePaymentId",
                table: "Subscriptions",
                column: "LastMolliePaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_MollieCustomerId",
                table: "Subscriptions",
                column: "MollieCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_MollieSubscriptionId",
                table: "Subscriptions",
                column: "MollieSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_MollieWebhookEvents_MollieResourceId",
                table: "MollieWebhookEvents",
                column: "MollieResourceId",
                unique: true,
                filter: "[ResourceType] = 'payment'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MollieWebhookEvents");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_LastMolliePaymentId",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_MollieCustomerId",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_MollieSubscriptionId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "BillingCity",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "BillingCountry",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "BillingStreet",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "BillingZipCode",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "LegalCompanyName",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "VatId",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "LastMolliePaymentId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "MollieCustomerId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "MollieMandateSignedAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "MollieSubscriptionId",
                table: "Subscriptions");

            migrationBuilder.RenameColumn(
                name: "MollieMandateId",
                table: "Subscriptions",
                newName: "StripeSubscriptionId");

            migrationBuilder.RenameColumn(
                name: "CrmCustomerId",
                table: "Subscriptions",
                newName: "StripeCustomerId");
        }
    }
}
