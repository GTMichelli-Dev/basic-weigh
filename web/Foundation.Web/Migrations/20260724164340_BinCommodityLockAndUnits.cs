using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foundation.Web.Migrations
{
    /// <inheritdoc />
    public partial class BinCommodityLockAndUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UnitName",
                table: "Commodities",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "UnitPounds",
                table: "Commodities",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BinCommodityLock",
                table: "AppSetup",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "AppSetup",
                keyColumn: "Id",
                keyValue: 1,
                column: "BinCommodityLock",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnitName",
                table: "Commodities");

            migrationBuilder.DropColumn(
                name: "UnitPounds",
                table: "Commodities");

            migrationBuilder.DropColumn(
                name: "BinCommodityLock",
                table: "AppSetup");
        }
    }
}
