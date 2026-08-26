using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foundation.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTareResetGates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Per-source gates on the "reset this truck's tare" choice. The
            // column default is false only because that is what SQLite writes
            // into the rows it back-fills; the UpdateData below turns all three
            // on for the one AppSetup row that exists, so an upgrading site
            // keeps offering the choice rather than silently losing it. A site
            // that wants tares applied automatically clears them in Setup.
            migrationBuilder.AddColumn<bool>(
                name: "AllowTareResetCard",
                table: "AppSetup",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowTareResetKiosk",
                table: "AppSetup",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowTareResetMobile",
                table: "AppSetup",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "AppSetup",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AllowTareResetCard", "AllowTareResetKiosk", "AllowTareResetMobile" },
                values: new object[] { true, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowTareResetCard",
                table: "AppSetup");

            migrationBuilder.DropColumn(
                name: "AllowTareResetKiosk",
                table: "AppSetup");

            migrationBuilder.DropColumn(
                name: "AllowTareResetMobile",
                table: "AppSetup");
        }
    }
}
