using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foundation.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEnableSpanish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Master switch for the bilingual driver screens. Defaults to false
            // so deploying this build changes nothing until a site turns it on
            // in Setup — the language column added alongside it is inert while
            // this is off. Additive: SQLite appends the column in place.
            migrationBuilder.AddColumn<bool>(
                name: "EnableSpanish",
                table: "AppSetup",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite has no DROP COLUMN, so EF rebuilds the table here. Only
            // reached on a deliberate rollback.
            migrationBuilder.DropColumn(
                name: "EnableSpanish",
                table: "AppSetup");
        }
    }
}
