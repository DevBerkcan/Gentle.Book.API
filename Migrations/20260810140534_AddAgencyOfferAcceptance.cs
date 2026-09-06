using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAgencyOfferAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcceptedAt",
                table: "SubscriptionRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcceptedByEmail",
                table: "SubscriptionRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AcceptedByUserId",
                table: "SubscriptionRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcceptedInterval",
                table: "SubscriptionRequests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcceptedIpAddress",
                table: "SubscriptionRequests",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AcceptedPrice",
                table: "SubscriptionRequests",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcceptedTermsVersion",
                table: "SubscriptionRequests",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OfferExpiresAt",
                table: "SubscriptionRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OfferedAnnualPrice",
                table: "SubscriptionRequests",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OfferedAt",
                table: "SubscriptionRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OfferedMonthlyPrice",
                table: "SubscriptionRequests",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcceptedAt",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "AcceptedByEmail",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "AcceptedByUserId",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "AcceptedInterval",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "AcceptedIpAddress",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "AcceptedPrice",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "AcceptedTermsVersion",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "OfferExpiresAt",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "OfferedAnnualPrice",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "OfferedAt",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "OfferedMonthlyPrice",
                table: "SubscriptionRequests");
        }
    }
}
