using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTrialAccessAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrialAccessInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcceptedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TermsVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PrivacyVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DpaVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BusinessConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TermsAccepted = table.Column<bool>(type: "bit", nullable: false),
                    PrivacyAcknowledged = table.Column<bool>(type: "bit", nullable: false),
                    DpaAccepted = table.Column<bool>(type: "bit", nullable: false),
                    NoAutomaticPaidConversionAcknowledged = table.Column<bool>(type: "bit", nullable: false),
                    PersonalNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrialAccessInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrialAccessInvitations_PlatformUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TrialAccessInvitations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrialAccessInvitations_TenantId_AcceptedAt",
                table: "TrialAccessInvitations",
                columns: new[] { "TenantId", "AcceptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TrialAccessInvitations_TokenHash",
                table: "TrialAccessInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrialAccessInvitations_UserId",
                table: "TrialAccessInvitations",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrialAccessInvitations");
        }
    }
}
