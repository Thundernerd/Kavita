using System;
using System.Threading.Tasks;
using Kavita.Common.EnvironmentInfo;
using Kavita.Database;
using Kavita.Models.Entities.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kavita.Server.ManualMigrations.v0._9._1;

/// <summary>
/// Early kobo-sync used a single <c>20260802110922_KoboSync</c> migration (synced books, reading state, converted-book cache).
/// The feature branch replaced that with the AppUserKobo* tables. Live DBs that already applied KoboSync must be
/// rewritten and the replacement EF IDs stamped so MigrateAsync does not try to add AllowKoboSync again.
/// </summary>
public static class ManualMigrateLegacyKoboSyncSchema
{
    private const string ManualName = nameof(ManualMigrateLegacyKoboSyncSchema);
    private const string LegacyMigrationId = "20260802110922_KoboSync";
    private const string FirstReplacementId = "20260802171940_AllowKoboSyncLibrary";

    public static async Task Migrate(DataContext context, ILogger<Program> logger)
    {
        if (await context.ManualMigrationHistory.AnyAsync(m => m.Name == ManualName))
        {
            return;
        }

        var hasLegacy = await MigrationIdExists(context, LegacyMigrationId);
        var hasReplacement = await MigrationIdExists(context, FirstReplacementId);
        if (!hasLegacy || hasReplacement)
        {
            return;
        }

        logger.LogCritical("Running {MigrationName} migration - Please be patient, this may take some time. This is not an error", ManualName);

        await using var tx = await context.Database.BeginTransactionAsync();

        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AppUserKoboSyncedChapter" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AppUserKoboSyncedChapter" PRIMARY KEY AUTOINCREMENT,
                "AppUserId" INTEGER NOT NULL,
                "ChapterId" INTEGER NOT NULL,
                CONSTRAINT "FK_AppUserKoboSyncedChapter_AspNetUsers_AppUserId" FOREIGN KEY ("AppUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AppUserKoboSyncedChapter_Chapter_ChapterId" FOREIGN KEY ("ChapterId") REFERENCES "Chapter" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppUserKoboSyncedChapter_AppUserId_ChapterId" ON "AppUserKoboSyncedChapter" ("AppUserId", "ChapterId");
            CREATE INDEX IF NOT EXISTS "IX_AppUserKoboSyncedChapter_ChapterId" ON "AppUserKoboSyncedChapter" ("ChapterId");

            CREATE TABLE IF NOT EXISTS "AppUserKoboArchivedChapter" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AppUserKoboArchivedChapter" PRIMARY KEY AUTOINCREMENT,
                "AppUserId" INTEGER NOT NULL,
                "ChapterId" INTEGER NOT NULL,
                "LastModifiedUtc" TEXT NOT NULL,
                "IsDeviceDeleted" INTEGER NOT NULL DEFAULT 0,
                CONSTRAINT "FK_AppUserKoboArchivedChapter_AspNetUsers_AppUserId" FOREIGN KEY ("AppUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AppUserKoboArchivedChapter_Chapter_ChapterId" FOREIGN KEY ("ChapterId") REFERENCES "Chapter" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppUserKoboArchivedChapter_AppUserId_ChapterId" ON "AppUserKoboArchivedChapter" ("AppUserId", "ChapterId");
            CREATE INDEX IF NOT EXISTS "IX_AppUserKoboArchivedChapter_ChapterId" ON "AppUserKoboArchivedChapter" ("ChapterId");

            CREATE TABLE IF NOT EXISTS "AppUserKoboTombstone" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AppUserKoboTombstone" PRIMARY KEY AUTOINCREMENT,
                "AppUserId" INTEGER NOT NULL,
                "ChapterId" INTEGER NOT NULL,
                "EntitlementId" TEXT NOT NULL,
                "Title" TEXT NULL,
                "CreatedUtc" TEXT NOT NULL,
                CONSTRAINT "FK_AppUserKoboTombstone_AspNetUsers_AppUserId" FOREIGN KEY ("AppUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppUserKoboTombstone_AppUserId_ChapterId" ON "AppUserKoboTombstone" ("AppUserId", "ChapterId");
            CREATE INDEX IF NOT EXISTS "IX_AppUserKoboTombstone_EntitlementId" ON "AppUserKoboTombstone" ("EntitlementId");

            CREATE TABLE IF NOT EXISTS "AppUserKoboTagTombstone" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AppUserKoboTagTombstone" PRIMARY KEY AUTOINCREMENT,
                "AppUserId" INTEGER NOT NULL,
                "TagId" TEXT NOT NULL,
                "LastModifiedUtc" TEXT NOT NULL,
                CONSTRAINT "FK_AppUserKoboTagTombstone_AspNetUsers_AppUserId" FOREIGN KEY ("AppUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppUserKoboTagTombstone_AppUserId_TagId" ON "AppUserKoboTagTombstone" ("AppUserId", "TagId");
            CREATE INDEX IF NOT EXISTS "IX_AppUserKoboTagTombstone_TagId" ON "AppUserKoboTagTombstone" ("TagId");

            CREATE TABLE IF NOT EXISTS "AppUserKoboReadingLocation" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AppUserKoboReadingLocation" PRIMARY KEY AUTOINCREMENT,
                "AppUserId" INTEGER NOT NULL,
                "ChapterId" INTEGER NOT NULL,
                "LocationValue" TEXT NULL,
                "LocationType" TEXT NULL,
                "LocationSource" TEXT NULL,
                CONSTRAINT "FK_AppUserKoboReadingLocation_AspNetUsers_AppUserId" FOREIGN KEY ("AppUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AppUserKoboReadingLocation_Chapter_ChapterId" FOREIGN KEY ("ChapterId") REFERENCES "Chapter" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppUserKoboReadingLocation_AppUserId_ChapterId" ON "AppUserKoboReadingLocation" ("AppUserId", "ChapterId");
            CREATE INDEX IF NOT EXISTS "IX_AppUserKoboReadingLocation_ChapterId" ON "AppUserKoboReadingLocation" ("ChapterId");

            INSERT OR IGNORE INTO "AppUserKoboSyncedChapter" ("AppUserId", "ChapterId")
            SELECT "AppUserId", "ChapterId" FROM "AppUserKoboSyncedBook";

            INSERT OR IGNORE INTO "AppUserKoboReadingLocation" ("AppUserId", "ChapterId", "LocationValue", "LocationType", "LocationSource")
            SELECT "AppUserId", "ChapterId", "LocationValue", "LocationType", "LocationSource" FROM "AppUserKoboReadingState";

            DROP TABLE IF EXISTS "AppUserKoboSyncedBook";
            DROP TABLE IF EXISTS "AppUserKoboReadingState";
            DROP TABLE IF EXISTS "KoboConvertedBook";
            DROP INDEX IF EXISTS "IX_Chapter_KoboUuid";
            """);

        await DropChapterKoboUuidIfPresent(context);

        await context.Database.ExecuteSqlRawAsync("""
            INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
            ('20260802171940_AllowKoboSyncLibrary', '10.0.9'),
            ('20260802185446_AppUserKoboSyncedChapter', '10.0.9'),
            ('20260802193049_AppUserKoboArchiveAndTombstone', '10.0.9'),
            ('20260802204823_AppUserKoboArchivedChapterDeviceDeleted', '10.0.9'),
            ('20260803105759_AppUserKoboTagTombstone', '10.0.9'),
            ('20260803172105_AppUserKoboReadingLocation', '10.0.9');
            """);

        await context.ManualMigrationHistory.AddAsync(new ManualMigrationHistory
        {
            Name = ManualName,
            ProductVersion = BuildInfo.Version.ToString(),
            RanAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        await tx.CommitAsync();

        logger.LogCritical("Running {MigrationName} migration - Completed. This is not an error", ManualName);
    }

    private static async Task<bool> MigrationIdExists(DataContext context, string migrationId)
    {
        await using var cmd = context.Database.GetDbConnection().CreateCommand();
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {
            await cmd.Connection.OpenAsync();
        }

        cmd.CommandText = """SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = $id""";
        var p = cmd.CreateParameter();
        p.ParameterName = "$id";
        p.Value = migrationId;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }

    private static async Task DropChapterKoboUuidIfPresent(DataContext context)
    {
        await using var cmd = context.Database.GetDbConnection().CreateCommand();
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {
            await cmd.Connection.OpenAsync();
        }

        cmd.CommandText = """SELECT COUNT(*) FROM pragma_table_info('Chapter') WHERE name = 'KoboUuid'""";
        var result = await cmd.ExecuteScalarAsync();
        if (Convert.ToInt64(result) == 0)
        {
            return;
        }

        await context.Database.ExecuteSqlRawAsync("""ALTER TABLE "Chapter" DROP COLUMN "KoboUuid";""");
    }
}
