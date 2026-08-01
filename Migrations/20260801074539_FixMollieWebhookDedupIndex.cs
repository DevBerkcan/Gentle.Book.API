using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixMollieWebhookDedupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MollieWebhookEvents_MollieResourceId",
                table: "MollieWebhookEvents");

            migrationBuilder.AlterColumn<string>(
                name: "ResultStatus",
                table: "MollieWebhookEvents",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MollieWebhookEvents_MollieResourceId_ResultStatus",
                table: "MollieWebhookEvents",
                columns: new[] { "MollieResourceId", "ResultStatus" },
                unique: true,
                filter: "[ResourceType] = 'payment' AND [ResultStatus] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MollieWebhookEvents_MollieResourceId_ResultStatus",
                table: "MollieWebhookEvents");

            migrationBuilder.AlterColumn<string>(
                name: "ResultStatus",
                table: "MollieWebhookEvents",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MollieWebhookEvents_MollieResourceId",
                table: "MollieWebhookEvents",
                column: "MollieResourceId",
                unique: true,
                filter: "[ResourceType] = 'payment'");
        }
    }
}
