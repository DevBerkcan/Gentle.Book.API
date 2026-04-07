using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GentleBook.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlockedTimeSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BlockDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedTimeSlots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BusinessHours",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DayOfWeek = table.Column<int>(type: "int", nullable: false),
                    OpenTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    CloseTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    IsOpen = table.Column<bool>(type: "bit", nullable: false),
                    BreakStartTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    BreakEndTime = table.Column<TimeOnly>(type: "time", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessHours", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastVisit = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotalBookings = table.Column<int>(type: "int", nullable: false),
                    NoShowCount = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Services",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Services", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ConfirmationSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReminderSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CustomerNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AdminNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bookings_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Bookings_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmailLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EmailType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailLogs_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.InsertData(
                table: "BusinessHours",
                columns: new[] { "Id", "BreakEndTime", "BreakStartTime", "CloseTime", "DayOfWeek", "IsOpen", "OpenTime" },
                values: new object[,]
                {
                    { new Guid("13a514e0-4b48-439f-b2f4-7c83bf06c40d"), null, null, new TimeOnly(20, 0, 0), 4, true, new TimeOnly(9, 0, 0) },
                    { new Guid("56e2422b-b8ca-4d0f-9afc-b4ea46140673"), null, null, new TimeOnly(18, 0, 0), 2, true, new TimeOnly(9, 0, 0) },
                    { new Guid("dbe7723d-35cc-485f-9e6e-04e3a0639f94"), null, null, new TimeOnly(0, 0, 0), 0, false, new TimeOnly(0, 0, 0) },
                    { new Guid("e0b75c2e-3d58-4f0c-82bd-24db70b471bd"), null, null, new TimeOnly(18, 0, 0), 1, true, new TimeOnly(9, 0, 0) },
                    { new Guid("e54cf1ea-6cef-40a5-9da1-82f5509b61db"), null, null, new TimeOnly(16, 0, 0), 6, true, new TimeOnly(9, 0, 0) },
                    { new Guid("ef78c874-bdc8-44f3-bb38-d5566581d2c9"), null, null, new TimeOnly(18, 0, 0), 3, true, new TimeOnly(9, 0, 0) },
                    { new Guid("fa184a8e-ed14-4edc-91bc-7455c0c9a29f"), null, null, new TimeOnly(18, 0, 0), 5, true, new TimeOnly(9, 0, 0) }
                });

            migrationBuilder.InsertData(
                table: "Services",
                columns: new[] { "Id", "CreatedAt", "Description", "DisplayOrder", "DurationMinutes", "IsActive", "Name", "Price", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-111111111111"), new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8130), "Klassischer Herrenhaarschnitt mit Styling", 1, 30, true, "Herrenschnitt", 35.00m, new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8130) },
                    { new Guid("22222222-2222-2222-2222-222222222222"), new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8150), "Professionelles Bart-Trimming und Konturenschneiden", 2, 20, true, "Bart Trimmen", 20.00m, new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8150) },
                    { new Guid("33333333-3333-3333-3333-333333333333"), new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8160), "Haarschnitt + Bart Trimmen", 3, 50, true, "Komplettpaket", 50.00m, new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8160) },
                    { new Guid("44444444-4444-4444-4444-444444444444"), new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8160), "Haarschnitt für Kinder bis 12 Jahre", 4, 30, true, "Kinder Haarschnitt", 25.00m, new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8160) }
                });

            migrationBuilder.InsertData(
                table: "Settings",
                columns: new[] { "Key", "Description", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { "ADMIN_EMAIL", "Admin Email-Adresse", new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8320), "dario@barberdario.com" },
                    { "BOOKING_INTERVAL_MINUTES", "Zeitintervall für Buchungen in Minuten", new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8320), "15" },
                    { "BUSINESS_ADDRESS", "Geschäftsadresse", new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8320), "Berliner Allee 43, 40212 Düsseldorf" },
                    { "BUSINESS_NAME", "Name des Geschäfts", new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8320), "Barber Dario" },
                    { "MAX_ADVANCE_BOOKING_DAYS", "Wie viele Tage im Voraus kann gebucht werden", new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8320), "60" },
                    { "MIN_ADVANCE_BOOKING_HOURS", "Mindestvorlauf für Buchungen in Stunden", new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8320), "24" },
                    { "REMINDER_HOURS_BEFORE", "Wann vor dem Termin wird die Erinnerung gesendet (Stunden)", new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8320), "24" },
                    { "TIMEZONE", "Zeitzone des Geschäfts", new DateTime(2025, 12, 27, 20, 27, 53, 687, DateTimeKind.Utc).AddTicks(8320), "Europe/Berlin" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedTimeSlots_BlockDate",
                table: "BlockedTimeSlots",
                column: "BlockDate");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_BookingDate",
                table: "Bookings",
                column: "BookingDate");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_BookingDate_Status",
                table: "Bookings",
                columns: new[] { "BookingDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CustomerId",
                table: "Bookings",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_ServiceId",
                table: "Bookings",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_Status",
                table: "Bookings",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessHours_DayOfWeek",
                table: "BusinessHours",
                column: "DayOfWeek",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Email",
                table: "Customers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Phone",
                table: "Customers",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_EmailLogs_BookingId",
                table: "EmailLogs",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailLogs_EmailType",
                table: "EmailLogs",
                column: "EmailType");

            migrationBuilder.CreateIndex(
                name: "IX_Services_IsActive",
                table: "Services",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedTimeSlots");

            migrationBuilder.DropTable(
                name: "BusinessHours");

            migrationBuilder.DropTable(
                name: "EmailLogs");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Bookings");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Services");
        }
    }
}
