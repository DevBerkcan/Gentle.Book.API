using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionDataRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OperationalDataDeletedAt",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetentionEndsAt",
                table: "Subscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RetentionWarningEmailSent",
                table: "Subscriptions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Bring subscriptions cancelled before this feature under the same 30-day policy.
            // If the deadline already passed, the daily retention job will purge them on its next run.
            migrationBuilder.Sql("""
                UPDATE [Subscriptions]
                SET [RetentionEndsAt] = DATEADD(day, 30, [CancelledAt])
                WHERE [Status] = 'Cancelled'
                  AND [CancelledAt] IS NOT NULL
                  AND [RetentionEndsAt] IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_RetentionEndsAt",
                table: "Subscriptions",
                column: "RetentionEndsAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_RetentionEndsAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "OperationalDataDeletedAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "RetentionEndsAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "RetentionWarningEmailSent",
                table: "Subscriptions");
        }
    }
}
