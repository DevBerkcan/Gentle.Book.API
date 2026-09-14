using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddChatbotMessageLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: 20 (not 0) — this backfills existing TenantSettings rows the same way
            // the TenantSettings.cs property initializer defaults new ones, so already-active
            // Agency tenants keep a working chatbot after this migration instead of being capped
            // to zero messages.
            migrationBuilder.AddColumn<int>(
                name: "ChatbotMaxMessagesPerConversation",
                table: "TenantSettings",
                type: "int",
                nullable: false,
                defaultValue: 20);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChatbotMaxMessagesPerConversation",
                table: "TenantSettings");
        }
    }
}
