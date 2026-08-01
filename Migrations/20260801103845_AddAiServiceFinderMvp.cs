using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAiServiceFinderMvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActionType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    InputJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OutputJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ConfirmedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfirmedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiConversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiKnowledgeSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Visibility = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ApprovalStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiKnowledgeSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IndustryProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndustryProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceFinderBookingDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CustomerNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConfirmedBookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceFinderBookingDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceFinderRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RuleType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ConditionJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ApprovalStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceFinderRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceFinderRules_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ServiceGuidances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuidanceType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    ApprovalStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceGuidances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceGuidances_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantIndustryCapabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapabilityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantIndustryCapabilities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TokenUsage = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiMessages_AiConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "AiConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiKnowledgeDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiKnowledgeDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiKnowledgeDocuments_AiKnowledgeSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "AiKnowledgeSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndustryCapabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IndustryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapabilityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DefaultEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndustryCapabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndustryCapabilities_IndustryProfiles_IndustryProfileId",
                        column: x => x.IndustryProfileId,
                        principalTable: "IndustryProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceFinderQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IndustryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QuestionKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    QuestionText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AnswerType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceFinderQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceFinderQuestions_IndustryProfiles_IndustryProfileId",
                        column: x => x.IndustryProfileId,
                        principalTable: "IndustryProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TenantIndustrySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrimaryIndustryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    IsFinderEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantIndustrySettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantIndustrySettings_IndustryProfiles_PrimaryIndustryProfileId",
                        column: x => x.PrimaryIndustryProfileId,
                        principalTable: "IndustryProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantIndustrySettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiKnowledgeChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SectionName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VectorReference = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiKnowledgeChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiKnowledgeChunks_AiKnowledgeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "AiKnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiActions_TenantId_CreatedOn",
                table: "AiActions",
                columns: new[] { "TenantId", "CreatedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_AiConversations_TenantId_CreatedAt",
                table: "AiConversations",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiKnowledgeChunks_DocumentId",
                table: "AiKnowledgeChunks",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_AiKnowledgeChunks_TenantId_DocumentId",
                table: "AiKnowledgeChunks",
                columns: new[] { "TenantId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiKnowledgeDocuments_SourceId",
                table: "AiKnowledgeDocuments",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AiKnowledgeDocuments_TenantId_SourceId_IsActive",
                table: "AiKnowledgeDocuments",
                columns: new[] { "TenantId", "SourceId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_AiKnowledgeSources_TenantId_Visibility_ApprovalStatus_Status",
                table: "AiKnowledgeSources",
                columns: new[] { "TenantId", "Visibility", "ApprovalStatus", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AiMessages_ConversationId_CreatedAt",
                table: "AiMessages",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsages_TenantId_Feature_CreatedOn",
                table: "AiUsages",
                columns: new[] { "TenantId", "Feature", "CreatedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_IndustryCapabilities_IndustryProfileId_CapabilityKey",
                table: "IndustryCapabilities",
                columns: new[] { "IndustryProfileId", "CapabilityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndustryProfiles_Key",
                table: "IndustryProfiles",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceFinderBookingDrafts_TenantId_Status_ExpiresAt",
                table: "ServiceFinderBookingDrafts",
                columns: new[] { "TenantId", "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceFinderQuestions_IndustryProfileId",
                table: "ServiceFinderQuestions",
                column: "IndustryProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceFinderQuestions_TenantId_IsActive_DisplayOrder",
                table: "ServiceFinderQuestions",
                columns: new[] { "TenantId", "IsActive", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceFinderQuestions_TenantId_QuestionKey",
                table: "ServiceFinderQuestions",
                columns: new[] { "TenantId", "QuestionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceFinderRules_ServiceId",
                table: "ServiceFinderRules",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceFinderRules_TenantId_IsActive_Priority",
                table: "ServiceFinderRules",
                columns: new[] { "TenantId", "IsActive", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceGuidances_ServiceId",
                table: "ServiceGuidances",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceGuidances_TenantId_ServiceId_GuidanceType_IsActive",
                table: "ServiceGuidances",
                columns: new[] { "TenantId", "ServiceId", "GuidanceType", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantIndustryCapabilities_TenantId_CapabilityKey",
                table: "TenantIndustryCapabilities",
                columns: new[] { "TenantId", "CapabilityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantIndustrySettings_PrimaryIndustryProfileId",
                table: "TenantIndustrySettings",
                column: "PrimaryIndustryProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantIndustrySettings_TenantId",
                table: "TenantIndustrySettings",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiActions");

            migrationBuilder.DropTable(
                name: "AiKnowledgeChunks");

            migrationBuilder.DropTable(
                name: "AiMessages");

            migrationBuilder.DropTable(
                name: "AiUsages");

            migrationBuilder.DropTable(
                name: "IndustryCapabilities");

            migrationBuilder.DropTable(
                name: "ServiceFinderBookingDrafts");

            migrationBuilder.DropTable(
                name: "ServiceFinderQuestions");

            migrationBuilder.DropTable(
                name: "ServiceFinderRules");

            migrationBuilder.DropTable(
                name: "ServiceGuidances");

            migrationBuilder.DropTable(
                name: "TenantIndustryCapabilities");

            migrationBuilder.DropTable(
                name: "TenantIndustrySettings");

            migrationBuilder.DropTable(
                name: "AiKnowledgeDocuments");

            migrationBuilder.DropTable(
                name: "AiConversations");

            migrationBuilder.DropTable(
                name: "IndustryProfiles");

            migrationBuilder.DropTable(
                name: "AiKnowledgeSources");
        }
    }
}
