using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foundation.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Site default language for the driver-facing screens. Purely
            // additive: SQLite appends the column in place, so no existing row
            // is rewritten and no other table is touched.
            //
            // defaultValue is "en" rather than the "" the EF scaffolder emits
            // for a non-nullable string, so an existing install reads as
            // explicitly English instead of blank. Either value behaves the
            // same at runtime — Lang.Normalize maps an unrecognised code to
            // English — so regenerating this migration with `dotnet ef` is safe.
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "AppSetup",
                type: "TEXT",
                maxLength: 5,
                nullable: false,
                defaultValue: "en");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite has no DROP COLUMN, so EF rebuilds the table here. Only
            // reached on a deliberate rollback.
            migrationBuilder.DropColumn(
                name: "Language",
                table: "AppSetup");
        }
    }
}
