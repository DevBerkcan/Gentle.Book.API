using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCancellationAndDunning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "Subscriptions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelRequestedAt",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DunningWarningEmailSent",
                table: "Subscriptions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FailedPaymentCount",
                table: "Subscriptions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastFailedMolliePaymentId",
                table: "Subscriptions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PastDueSince",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_CancelRequestedAt",
                table: "Subscriptions",
                column: "CancelRequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_PastDueSince",
                table: "Subscriptions",
                column: "PastDueSince");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_CancelRequestedAt",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_PastDueSince",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "CancelRequestedAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "DunningWarningEmailSent",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "FailedPaymentCount",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "LastFailedMolliePaymentId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "PastDueSince",
                table: "Subscriptions");
        }
    }
}
