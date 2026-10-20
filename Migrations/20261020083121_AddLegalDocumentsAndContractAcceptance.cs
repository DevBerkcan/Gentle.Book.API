using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalDocumentsAndContractAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegalDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    FullText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Sha256Hash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReleasedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContractAcceptances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TermsDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrivacyDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DpaDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Plan = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Interval = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PriceSnapshot = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CurrencySnapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BusinessConfirmed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractAcceptances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractAcceptances_LegalDocuments_DpaDocumentId",
                        column: x => x.DpaDocumentId,
                        principalTable: "LegalDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractAcceptances_LegalDocuments_PrivacyDocumentId",
                        column: x => x.PrivacyDocumentId,
                        principalTable: "LegalDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractAcceptances_LegalDocuments_TermsDocumentId",
                        column: x => x.TermsDocumentId,
                        principalTable: "LegalDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractAcceptances_DpaDocumentId",
                table: "ContractAcceptances",
                column: "DpaDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractAcceptances_PrivacyDocumentId",
                table: "ContractAcceptances",
                column: "PrivacyDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractAcceptances_SubscriptionId",
                table: "ContractAcceptances",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractAcceptances_TermsDocumentId",
                table: "ContractAcceptances",
                column: "TermsDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocuments_Type_Version_Language",
                table: "LegalDocuments",
                columns: new[] { "Type", "Version", "Language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContractAcceptances");

            migrationBuilder.DropTable(
                name: "LegalDocuments");
        }
    }
}
