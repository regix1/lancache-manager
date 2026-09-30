namespace LancacheManager.Infrastructure.Data;

/// <summary>
/// PostgreSQL statements for the one-time download-history upgrade. The five-minute session gap
/// matches <c>SESSION_GAP_MINUTES</c> in <c>rust-processor/src/log_processor.rs</c>.
/// </summary>
internal static class DownloadHistoryUpgradeSql
{
    // One batch re-points this many absorbed rows' LogEntries while writers that do not ask the
    // conflict checker wait on the table locks. The PostgreSQL 17 sandbox measured 1.55 seconds
    // for 5,000 absorbed rows across 3.05 million LogEntries.
    internal const int BatchSize = 5000;

    internal const string DuplicateCheckIndexState =
        "WITH current_index AS MATERIALIZED (SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = current_schema() AND c.relname = 'IX_LogEntries_DuplicateCheck') SELECT CASE WHEN EXISTS (SELECT 1 FROM current_index c JOIN pg_index i ON i.indexrelid = c.oid WHERE i.indisvalid AND pg_get_indexdef(c.oid) LIKE '%md5(%') THEN 'ready' ELSE 'rebuild' END AS \"Value\"";

    internal const string TerminateOrphanIndexBuilds =
        "SELECT count(pg_terminate_backend(p.pid))::int AS \"Value\" FROM pg_stat_progress_create_index p WHERE p.relid = to_regclass('\"LogEntries\"') AND p.pid <> pg_backend_pid()";

    /// <summary>
    /// The MD5 expression avoids PostgreSQL btree version 4's 2,704-byte row limit: a raw
    /// 2,000-character range beside a long URL would otherwise reject the ingest row. The plain
    /// build and swap share one transaction, so the old index remains intact on a crash. The job
    /// owns the global operation slot while writers outside the conflict checker wait for the build.
    /// An index build left on the server by a killed process must be stopped first because this job
    /// is the only post-migration builder for LogEntries. The final rename preserves the name used
    /// by the EF model and future migrations.
    /// </summary>
    internal const string DropMd5IndexIfExists =
        "DROP INDEX IF EXISTS \"IX_LogEntries_DuplicateCheck_Md5\"";

    internal const string CreateMd5Index =
        "CREATE INDEX \"IX_LogEntries_DuplicateCheck_Md5\" ON \"LogEntries\" (\"ClientIp\", \"Service\", \"Timestamp\", \"Url\", \"BytesServed\", \"Datasource\", md5(COALESCE(\"HttpRange\", '')))";

    internal const string DropOldIndexIfExists =
        "DROP INDEX IF EXISTS \"IX_LogEntries_DuplicateCheck\"";

    internal const string RenameMd5Index =
        "ALTER INDEX \"IX_LogEntries_DuplicateCheck_Md5\" RENAME TO \"IX_LogEntries_DuplicateCheck\"";

    internal const string PlanExists =
        "SELECT to_regclass('\"DownloadSessionMergePlan\"') IS NOT NULL AS \"Value\"";

    internal const string PlanIsEmpty =
        "SELECT NOT EXISTS (SELECT 1 FROM \"DownloadSessionMergePlan\") AS \"Value\"";

    internal const string RemainingPlanRows =
        "SELECT count(*) AS \"Value\" FROM \"DownloadSessionMergePlan\"";

    internal const string DropPlan = "DROP TABLE IF EXISTS \"DownloadSessionMergePlan\"";

    internal const string CreatePlan = """
        CREATE TABLE "DownloadSessionMergePlan" (
            "AbsorbedId" bigint PRIMARY KEY,
            "SurvivorId" bigint NOT NULL
        );
        INSERT INTO "DownloadSessionMergePlan" ("AbsorbedId", "SurvivorId")
        WITH keyed AS (
            SELECT d."Id", d."StartTimeUtc", d."EndTimeUtc",
                   d."Datasource", d."ClientIp", d."Service", d."DepotId", d."GameAppId",
                   d."GameName", d."EpicAppId", d."XboxProductId", d."IsEvicted",
                   CASE
                     WHEN d."GameName" IS NULL AND d."DepotId" IS NULL AND d."EpicAppId" IS NULL
                          AND lower(d."Service") LIKE '%epic%'
                       THEN array_to_string((array_remove(string_to_array(d."LastUrl", '/'), ''))[1:5], '/')
                     WHEN d."GameName" IS NULL AND d."DepotId" IS NULL AND lower(d."Service") = 'blizzard'
                       THEN substring(lower(d."LastUrl") from '/tpr/([^/]+)/')
                   END AS "UrlKey"
            FROM "Downloads" d
        ),
        ordered AS (
            SELECT k."Id", k."StartTimeUtc", k."EndTimeUtc", k."Datasource", k."ClientIp",
                   k."Service", k."DepotId", k."GameAppId", k."GameName", k."EpicAppId",
                   k."XboxProductId", k."IsEvicted", k."UrlKey",
                   MAX(k."EndTimeUtc") OVER (
                       PARTITION BY k."Datasource", k."ClientIp", k."Service", k."DepotId",
                                    k."GameAppId", k."GameName", k."EpicAppId", k."XboxProductId",
                                    k."IsEvicted", k."UrlKey"
                       ORDER BY k."StartTimeUtc", k."Id"
                       ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS "PrevEnd"
            FROM keyed k
        ),
        flagged AS (
            SELECT o."Id", o."StartTimeUtc", o."EndTimeUtc", o."Datasource", o."ClientIp",
                   o."Service", o."DepotId", o."GameAppId", o."GameName", o."EpicAppId",
                   o."XboxProductId", o."IsEvicted", o."UrlKey", o."PrevEnd",
                   CASE
                     WHEN o."PrevEnd" IS NULL
                          OR o."StartTimeUtc" > o."PrevEnd" + interval '5 minutes'
                       THEN 1 ELSE 0
                   END AS "Starts"
            FROM ordered o
        ),
        islands AS (
            SELECT f."Id", f."Datasource", f."ClientIp", f."Service", f."DepotId",
                   f."GameAppId", f."GameName", f."EpicAppId", f."XboxProductId",
                   f."IsEvicted", f."UrlKey",
                   SUM(f."Starts") OVER (
                       PARTITION BY f."Datasource", f."ClientIp", f."Service", f."DepotId",
                                    f."GameAppId", f."GameName", f."EpicAppId", f."XboxProductId",
                                    f."IsEvicted", f."UrlKey"
                       ORDER BY f."StartTimeUtc", f."Id") AS "Island"
            FROM flagged f
        ),
        survivors AS (
            SELECT i."Id",
                   MIN(i."Id") OVER (
                       PARTITION BY i."Datasource", i."ClientIp", i."Service", i."DepotId",
                                    i."GameAppId", i."GameName", i."EpicAppId", i."XboxProductId",
                                    i."IsEvicted", i."UrlKey", i."Island") AS "SurvivorId"
            FROM islands i
        )
        SELECT s."Id", s."SurvivorId"
        FROM survivors s
        WHERE s."Id" <> s."SurvivorId";
        """;

    // The plan records identities at plan time. A resolver may name part of an island before the
    // batch runs, so rows that differ only by resolver-written values still belong to one download.
    // If members gained different non-placeholder titles, only strictly equal pairs may fold.
    internal const string PrepareBatch = """
        CREATE TEMP TABLE "DownloadMergeSlice" (
            "AbsorbedId" bigint PRIMARY KEY,
            "SurvivorId" bigint NOT NULL
        ) ON COMMIT DROP;
        INSERT INTO "DownloadMergeSlice" ("AbsorbedId", "SurvivorId")
        SELECT "AbsorbedId", "SurvivorId"
        FROM "DownloadSessionMergePlan"
        ORDER BY "AbsorbedId"
        LIMIT @batchSize;
        CREATE TEMP TABLE "DownloadMergeBatch" (
            "AbsorbedId" bigint PRIMARY KEY,
            "SurvivorId" bigint NOT NULL
        ) ON COMMIT DROP;
        INSERT INTO "DownloadMergeBatch" ("AbsorbedId", "SurvivorId")
        SELECT s."AbsorbedId", s."SurvivorId"
        FROM "DownloadMergeSlice" s
        JOIN "Downloads" a ON a."Id" = s."AbsorbedId"
        JOIN "Downloads" v ON v."Id" = s."SurvivorId"
        WHERE a."Datasource" = v."Datasource"
          AND a."ClientIp" = v."ClientIp"
          AND a."DepotId" IS NOT DISTINCT FROM v."DepotId"
          AND a."IsEvicted" = v."IsEvicted"
          AND (a."Service" = v."Service"
               OR (a."Service" IN ('wsus', 'xboxlive') AND v."Service" = 'xbox')
               OR (v."Service" IN ('wsus', 'xboxlive') AND a."Service" = 'xbox'))
          AND (a."GameAppId" IS NOT DISTINCT FROM v."GameAppId"
               OR a."GameAppId" IS NULL OR v."GameAppId" IS NULL)
          AND (a."EpicAppId" IS NOT DISTINCT FROM v."EpicAppId"
               OR a."EpicAppId" IS NULL OR v."EpicAppId" IS NULL)
          AND (a."XboxProductId" IS NOT DISTINCT FROM v."XboxProductId"
               OR a."XboxProductId" IS NULL OR v."XboxProductId" IS NULL)
          AND (a."GameName" IS NOT DISTINCT FROM v."GameName"
               OR a."GameName" IS NULL OR v."GameName" IS NULL
               OR a."GameName" LIKE 'Steam App %' OR v."GameName" LIKE 'Steam App %');
        DELETE FROM "DownloadMergeBatch" b
        USING "Downloads" a, "Downloads" v
        WHERE a."Id" = b."AbsorbedId"
          AND v."Id" = b."SurvivorId"
          AND b."SurvivorId" IN (
              SELECT m."SurvivorId"
              FROM (
                  SELECT b2."SurvivorId", d."GameName", d."GameAppId", d."EpicAppId", d."XboxProductId"
                  FROM "DownloadMergeBatch" b2
                  JOIN "Downloads" d ON d."Id" = b2."AbsorbedId"
                  UNION ALL
                  SELECT d."Id", d."GameName", d."GameAppId", d."EpicAppId", d."XboxProductId"
                  FROM "Downloads" d
                  WHERE d."Id" IN (SELECT "SurvivorId" FROM "DownloadMergeBatch")
              ) m
              GROUP BY m."SurvivorId"
              HAVING count(DISTINCT m."GameName") FILTER (WHERE m."GameName" NOT LIKE 'Steam App %') > 1
                  OR count(DISTINCT m."GameAppId") > 1
                  OR count(DISTINCT m."EpicAppId") > 1
                  OR count(DISTINCT m."XboxProductId") > 1)
          AND NOT (
              a."Service" = v."Service"
              AND a."GameName" IS NOT DISTINCT FROM v."GameName"
              AND a."GameAppId" IS NOT DISTINCT FROM v."GameAppId"
              AND a."EpicAppId" IS NOT DISTINCT FROM v."EpicAppId"
              AND a."XboxProductId" IS NOT DISTINCT FROM v."XboxProductId");
        """;

    internal const string SkippedInBatch =
        "SELECT (SELECT count(*) FROM \"DownloadMergeSlice\") - (SELECT count(*) FROM \"DownloadMergeBatch\") AS \"Value\"";

    internal const string FoldBatch = """
        WITH members AS (
            SELECT b."SurvivorId", d."Id", d."StartTimeUtc", d."EndTimeUtc", d."CacheHitBytes",
                   d."CacheMissBytes", d."GameImageUrl", d."LastUrl", d."IsActive", d."Service",
                   d."GameName", d."GameAppId", d."EpicAppId", d."XboxProductId"
            FROM "DownloadMergeBatch" b
            JOIN "Downloads" d ON d."Id" = b."AbsorbedId"
            UNION ALL
            SELECT d."Id", d."Id", d."StartTimeUtc", d."EndTimeUtc", d."CacheHitBytes",
                   d."CacheMissBytes", d."GameImageUrl", d."LastUrl", d."IsActive", d."Service",
                   d."GameName", d."GameAppId", d."EpicAppId", d."XboxProductId"
            FROM "Downloads" d
            WHERE d."Id" IN (SELECT DISTINCT "SurvivorId" FROM "DownloadMergeBatch")
        ),
        totals AS (
            SELECT "SurvivorId", MIN("StartTimeUtc") AS s, MAX("EndTimeUtc") AS e,
                   SUM("CacheHitBytes") AS hit, SUM("CacheMissBytes") AS miss,
                   MAX("GameImageUrl") AS img,
                   CASE WHEN bool_or("Service" = 'xbox') THEN 'xbox' ELSE MIN("Service") END AS svc,
                   COALESCE(
                       MAX("GameName") FILTER (WHERE "GameName" NOT LIKE 'Steam App %'),
                       MAX("GameName")) AS name,
                   MAX("GameAppId") AS app, MAX("EpicAppId") AS epic,
                   MAX("XboxProductId") AS product
            FROM members
            GROUP BY "SurvivorId"
        ),
        latest AS (
            SELECT DISTINCT ON ("SurvivorId") "SurvivorId", "LastUrl", "IsActive"
            FROM members
            ORDER BY "SurvivorId", "EndTimeUtc" DESC, "Id" DESC
        )
        UPDATE "Downloads" d
        SET "StartTimeUtc" = t.s,
            "EndTimeUtc" = t.e,
            "CacheHitBytes" = t.hit,
            "CacheMissBytes" = t.miss,
            "GameImageUrl" = COALESCE(d."GameImageUrl", t.img),
            "LastUrl" = l."LastUrl",
            "IsActive" = l."IsActive",
            "Service" = t.svc,
            "GameName" = t.name,
            "GameAppId" = t.app,
            "EpicAppId" = t.epic,
            "XboxProductId" = t.product
        FROM totals t
        JOIN latest l ON l."SurvivorId" = t."SurvivorId"
        WHERE d."Id" = t."SurvivorId";
        UPDATE "LogEntries" e
        SET "DownloadId" = b."SurvivorId"
        FROM "DownloadMergeBatch" b
        WHERE e."DownloadId" = b."AbsorbedId";
        INSERT INTO "EventDownloads" ("EventId", "DownloadId", "TaggedAtUtc", "AutoTagged")
        SELECT ed."EventId", b."SurvivorId", MIN(ed."TaggedAtUtc"), bool_and(ed."AutoTagged")
        FROM "EventDownloads" ed
        JOIN "DownloadMergeBatch" b ON ed."DownloadId" = b."AbsorbedId"
        GROUP BY ed."EventId", b."SurvivorId"
        ON CONFLICT ("EventId", "DownloadId") DO UPDATE
        SET "AutoTagged" = "EventDownloads"."AutoTagged" AND EXCLUDED."AutoTagged",
            "TaggedAtUtc" = LEAST("EventDownloads"."TaggedAtUtc", EXCLUDED."TaggedAtUtc");
        DELETE FROM "Downloads" d
        USING "DownloadMergeBatch" b
        WHERE d."Id" = b."AbsorbedId";
        DELETE FROM "DownloadSessionMergePlan" p
        USING "DownloadMergeSlice" s
        WHERE p."AbsorbedId" = s."AbsorbedId";
        """;
}
