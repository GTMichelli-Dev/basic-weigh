using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foundation.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddProxCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CardNumber",
                table: "Transactions",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RecycleCards",
                table: "AppSetup",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseCardReader",
                table: "AppSetup",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Cards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CardNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    Issued = table.Column<bool>(type: "INTEGER", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IssuedBy = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    RecycleMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "Default"),
                    Customer = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Commodity = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Carrier = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    TruckId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Destination = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Bin = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    OpenTicket = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    LastTicket = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CardCustomValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CardId = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomFieldId = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardCustomValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardCustomValues_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CardCustomValues_CustomFields_CustomFieldId",
                        column: x => x.CustomFieldId,
                        principalTable: "CustomFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "AppSetup",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "RecycleCards", "UseCardReader" },
                values: new object[] { false, false });

            migrationBuilder.CreateIndex(
                name: "IX_CardCustomValues_CardId_CustomFieldId",
                table: "CardCustomValues",
                columns: new[] { "CardId", "CustomFieldId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CardCustomValues_CustomFieldId",
                table: "CardCustomValues",
                column: "CustomFieldId");

            migrationBuilder.CreateIndex(
                name: "IX_Cards_CardNumber",
                table: "Cards",
                column: "CardNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cards_OpenTicket",
                table: "Cards",
                column: "OpenTicket");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardCustomValues");

            migrationBuilder.DropTable(
                name: "Cards");

            migrationBuilder.DropColumn(
                name: "CardNumber",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "RecycleCards",
                table: "AppSetup");

            migrationBuilder.DropColumn(
                name: "UseCardReader",
                table: "AppSetup");
        }
    }
}
