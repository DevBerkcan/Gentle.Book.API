using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class ExpandIntakeFormsAndVouchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PercentageValue",
                table: "Vouchers",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LoyaltyRewardEveryNVisits",
                table: "TenantSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LoyaltyRewardType",
                table: "TenantSettings",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "MonetaryValue");

            migrationBuilder.AddColumn<decimal>(
                name: "LoyaltyRewardValue",
                table: "TenantSettings",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "IntakeFormFields",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConditionalOnFieldId",
                table: "IntakeFormFields",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConditionalOnValue",
                table: "IntakeFormFields",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FormType",
                table: "IntakeFormFields",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "IntakeFormReminderSentAt",
                table: "Bookings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntakeFormFields_CategoryId",
                table: "IntakeFormFields",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeFormFields_ConditionalOnFieldId",
                table: "IntakeFormFields",
                column: "ConditionalOnFieldId");

            migrationBuilder.AddForeignKey(
                name: "FK_IntakeFormFields_IntakeFormFields_ConditionalOnFieldId",
                table: "IntakeFormFields",
                column: "ConditionalOnFieldId",
                principalTable: "IntakeFormFields",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IntakeFormFields_ServiceCategories_CategoryId",
                table: "IntakeFormFields",
                column: "CategoryId",
                principalTable: "ServiceCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IntakeFormFields_IntakeFormFields_ConditionalOnFieldId",
                table: "IntakeFormFields");

            migrationBuilder.DropForeignKey(
                name: "FK_IntakeFormFields_ServiceCategories_CategoryId",
                table: "IntakeFormFields");

            migrationBuilder.DropIndex(
                name: "IX_IntakeFormFields_CategoryId",
                table: "IntakeFormFields");

            migrationBuilder.DropIndex(
                name: "IX_IntakeFormFields_ConditionalOnFieldId",
                table: "IntakeFormFields");

            migrationBuilder.DropColumn(
                name: "PercentageValue",
                table: "Vouchers");

            migrationBuilder.DropColumn(
                name: "LoyaltyRewardEveryNVisits",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "LoyaltyRewardType",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "LoyaltyRewardValue",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "IntakeFormFields");

            migrationBuilder.DropColumn(
                name: "ConditionalOnFieldId",
                table: "IntakeFormFields");

            migrationBuilder.DropColumn(
                name: "ConditionalOnValue",
                table: "IntakeFormFields");

            migrationBuilder.DropColumn(
                name: "FormType",
                table: "IntakeFormFields");

            migrationBuilder.DropColumn(
                name: "IntakeFormReminderSentAt",
                table: "Bookings");
        }
    }
}
