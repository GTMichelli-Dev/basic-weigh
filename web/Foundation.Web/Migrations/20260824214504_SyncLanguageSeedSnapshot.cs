using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foundation.Web.Migrations
{
    /// <inheritdoc />
    public partial class SyncLanguageSeedSnapshot : Migration
    {
        // Deliberately empty. AddLanguage and AddEnableSpanish added both
        // columns by hand and never regenerated the model snapshot, so the
        // snapshot's seed row lacked Language and EnableSpanish while the model
        // had them. EF reads that as a pending model change and Migrate()
        // throws PendingModelChangesWarning on startup, which crash-loops the
        // service. The fix is the regenerated snapshot beside this file; the
        // migration itself has nothing to do.
        //
        // The scaffolder wanted to UpdateData AppSetup.Language to "en" here.
        // That is dropped on purpose: both columns already exist with the right
        // values (AddLanguage's column default is "en", AddEnableSpanish's is
        // false), so the update is redundant on every real database — and on a
        // site where an operator picked Spanish in Setup it would silently
        // reset them to English on the next deploy.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
