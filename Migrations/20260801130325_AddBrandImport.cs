using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastBrandAnalysisOn",
                table: "TenantSettings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastBrandAnalysisStatus",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WebsiteConsentConfirmed",
                table: "TenantSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "WebsiteConsentConfirmedAt",
                table: "TenantSettings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WebsiteConsentConfirmedBy",
                table: "TenantSettings",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BrandAssetCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetType = table.Column<int>(type: "int", nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    DiscoveryHint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsSelected = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandAssetCandidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BrandImportJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ErrorMessageSafe = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    StartedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandImportJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BrandImportResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WebsiteTitle = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    BrandStyle = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DetectedDataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AppliedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandImportResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BrandThemeProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TemplateId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ThemeJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsSelected = table.Column<bool>(type: "bit", nullable: false),
                    IsApplied = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AppliedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandThemeProposals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrandAssetCandidates_TenantId_ImportResultId",
                table: "BrandAssetCandidates",
                columns: new[] { "TenantId", "ImportResultId" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandImportJobs_TenantId_SourceUrl",
                table: "BrandImportJobs",
                columns: new[] { "TenantId", "SourceUrl" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandImportJobs_TenantId_Status",
                table: "BrandImportJobs",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandImportResults_TenantId_JobId",
                table: "BrandImportResults",
                columns: new[] { "TenantId", "JobId" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandThemeProposals_TenantId_ImportResultId",
                table: "BrandThemeProposals",
                columns: new[] { "TenantId", "ImportResultId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandAssetCandidates");

            migrationBuilder.DropTable(
                name: "BrandImportJobs");

            migrationBuilder.DropTable(
                name: "BrandImportResults");

            migrationBuilder.DropTable(
                name: "BrandThemeProposals");

            migrationBuilder.DropColumn(
                name: "LastBrandAnalysisOn",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "LastBrandAnalysisStatus",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "WebsiteConsentConfirmed",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "WebsiteConsentConfirmedAt",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "WebsiteConsentConfirmedBy",
                table: "TenantSettings");
        }
    }
}
