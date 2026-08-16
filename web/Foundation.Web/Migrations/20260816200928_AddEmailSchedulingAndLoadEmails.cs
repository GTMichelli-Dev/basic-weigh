using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foundation.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailSchedulingAndLoadEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    SecurityMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PasswordProtected = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FromAddress = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FromName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ReplyTo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastSendUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoadEmailRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    Recipients = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CustomerFilter = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CommodityFilter = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SubjectTemplate = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoadEmailRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    Recipients = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Cc = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Frequency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    SendHour = table.Column<int>(type: "INTEGER", nullable: false),
                    SendMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    DayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    DayOfMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupCommoditiesIntoSheets = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeGrossTare = table.Column<bool>(type: "INTEGER", nullable: false),
                    SendWhenEmpty = table.Column<bool>(type: "INTEGER", nullable: false),
                    CustomerFilter = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CommodityFilter = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SiteId = table.Column<int>(type: "INTEGER", nullable: true),
                    SubjectTemplate = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoadEmailLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ticket = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoadEmailLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoadEmailLogs_LoadEmailRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "LoadEmailRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "EmailSettings",
                columns: new[] { "Id", "Enabled", "FromAddress", "FromName", "Host", "LastError", "LastSendUtc", "PasswordProtected", "Port", "ReplyTo", "SecurityMode", "Username" },
                values: new object[] { 1, false, null, null, "mail.smtp2go.com", null, null, null, 2525, null, "StartTls", null });

            migrationBuilder.CreateIndex(
                name: "IX_LoadEmailLogs_RuleId",
                table: "LoadEmailLogs",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadEmailLogs_Ticket_RuleId",
                table: "LoadEmailLogs",
                columns: new[] { "Ticket", "RuleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoadEmailRules_Name",
                table: "LoadEmailRules",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportSchedules_Name",
                table: "ReportSchedules",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailSettings");

            migrationBuilder.DropTable(
                name: "LoadEmailLogs");

            migrationBuilder.DropTable(
                name: "ReportSchedules");

            migrationBuilder.DropTable(
                name: "LoadEmailRules");
        }
    }
}
