using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UseCanceledSpelling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migration)
        {
            // These Status columns are read back as enum names. A stored name that is not one of today's names was
            // written before the canceled members were renamed, so the row ends canceled instead of failing the read.
            // Such a schedule run also kept the sentence the old version wrote for it, so it gets the canceled key a
            // canceled run stores today and reads like one.
            migration.Sql("UPDATE \"ScheduleExecutions\" SET \"Status\" = 'Canceled', \"Detail\" = 'signalr.generic.canceled' WHERE \"Status\" NOT IN ('Pending', 'Running', 'Canceling', 'Completed', 'Failed', 'Canceled', 'Waiting', 'Skipped')");
            migration.Sql("UPDATE \"PrefillSessions\" SET \"Status\" = 'Canceled' WHERE \"Status\" NOT IN ('Active', 'Terminated', 'Orphaned', 'Cleaned', 'Canceled')");
            migration.Sql("UPDATE \"PrefillHistoryEntries\" SET \"Status\" = 'Canceled' WHERE \"Status\" NOT IN ('InProgress', 'Completed', 'Cached', 'Failed', 'Canceled', 'Skipped')");

            // A finished run ends completed, failed or canceled, so any other state on a finished row is the
            // canceled state written before the rename; its saved snapshot gets the same state.
            migration.Sql("UPDATE \"PrefillRuns\" SET \"State\" = 'canceled', \"SnapshotJson\" = jsonb_set(\"SnapshotJson\"::jsonb, '{State}', '\"canceled\"')::text WHERE \"CompletedAtUtc\" IS NOT NULL AND \"State\" NOT IN ('completed', 'failed', 'canceled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migration)
        {
            // The replaced values are not kept, so the rows stay canceled.
        }
    }
}
