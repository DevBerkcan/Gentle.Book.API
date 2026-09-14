using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddChatbotVisitorDailyLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: 30 (not 0) — matches TenantSettings.cs's property initializer so
            // existing Agency tenants' chatbots keep working after this migration instead of being
            // capped to zero messages per visitor per day (see AddChatbotMessageLimit migration,
            // which had the same pitfall).
            migrationBuilder.AddColumn<int>(
                name: "ChatbotMaxMessagesPerVisitorPerDay",
                table: "TenantSettings",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "VisitorHash",
                table: "AiConversations",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChatbotMaxMessagesPerVisitorPerDay",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "VisitorHash",
                table: "AiConversations");
        }
    }
}
