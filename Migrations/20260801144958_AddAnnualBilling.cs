using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAnnualBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Interval",
                table: "Subscriptions",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Monthly");

            migrationBuilder.AddColumn<string>(
                name: "Interval",
                table: "SubscriptionRequests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Monthly");

            migrationBuilder.AddColumn<decimal>(
                name: "AnnualPrice",
                table: "PlanPrices",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Seed AnnualPrice for any existing PlanPrice override rows using the same 20%-off
            // formula the marketing website advertises — SuperAdmin can fine-tune afterward via
            // the pricing editor, exactly like MonthlyPrice already works.
            migrationBuilder.Sql("UPDATE PlanPrices SET AnnualPrice = ROUND(MonthlyPrice * 0.8, 0) * 12 WHERE AnnualPrice = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Interval",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "Interval",
                table: "SubscriptionRequests");

            migrationBuilder.DropColumn(
                name: "AnnualPrice",
                table: "PlanPrices");
        }
    }
}
