use anyhow::Result;
use futures_util::TryStreamExt;
use sqlx::{PgPool, Postgres, QueryBuilder, Row};
use std::collections::HashMap;

use crate::cache_utils;

#[derive(Debug)]
pub(crate) struct DownloadRecord {
    pub(crate) service: String,
    pub(crate) game_app_id: u32,
    pub(crate) game_name: String,
    pub(crate) url: String,
    pub(crate) depot_id: Option<u32>,
    /// Max bytes observed for this URL (`MAX(LogEntries.BytesServed)`); sizes the probe chunk
    /// count via `cache_utils::probe_chunks_for_bytes`. 0 means unknown → falls back to floor.
    pub(crate) bytes_served: i64,
}

#[derive(Debug)]
pub(crate) struct EpicDownloadRecord {
    pub(crate) epic_app_id: String,
    pub(crate) game_name: String,
    pub(crate) service: String,
    pub(crate) url: String,
    /// Max bytes observed for this URL (`MAX(LogEntries.BytesServed)`); sizes the probe chunk count.
    pub(crate) bytes_served: i64,
}

/// Name-keyed game record for services without an AppId or EpicAppId (Blizzard, Riot, Xbox).
/// Identity = (Service, GameName); GameAppId/EpicAppId are both NULL on the Download.
/// Mirrors `EpicDownloadRecord` but drops the `epic_app_id` column.
///
/// `service` (Downloads.Service) is the IDENTITY used for the (service, game_name) key and the
/// removal gate. `cache_service` (LogEntries.Service) is the CACHE-HASH service used to derive
/// cache file paths, because cache files are hashed `(service, url)`. For Blizzard/Riot the two
/// are equal; for Xbox the identity is `xbox` while the cache-hash service stays `wsus` (Xbox
/// content is delivered as lancache-tagged `wsus` traffic). Splitting them is load-bearing — using
/// the identity for hashing would miss every Xbox cache file.
#[derive(Debug)]
pub(crate) struct NamedDownloadRecord {
    pub(crate) service: String,
    pub(crate) cache_service: String,
    pub(crate) game_name: String,
    pub(crate) url: String,
    /// Max bytes observed for this URL (`MAX(LogEntries.BytesServed)`); sizes the probe chunk count.
    pub(crate) bytes_served: i64,
}

/// Returns the per-game record map directly. Rows are streamed off the connection straight
/// into the map: fetch_all would buffer every GROUP BY row (one per unique URL, millions on a
/// large library) as PgRows and then again as a flat Vec before grouping.
pub(crate) async fn query_game_downloads(
    pool: &PgPool,
    max_urls_per_game: Option<usize>,
    excluded_game_ids: &[u32],
) -> Result<HashMap<u32, Vec<DownloadRecord>>> {
    eprintln!("Querying LogEntries for game URLs...");

    if !excluded_game_ids.is_empty() {
        eprintln!(
            "Excluding {} already-detected games (incremental scan)",
            excluded_game_ids.len()
        );
    }

    let excluded_unknown_depot_ids: Vec<i64> = if excluded_game_ids.is_empty() {
        Vec::new()
    } else {
        let mut unknown_exclusions = QueryBuilder::<Postgres>::new(
            r#"
            SELECT "GameAppId"
            FROM "CachedGameDetections"
            WHERE "GameName" LIKE 'Unknown Game (Depot %)'
              AND "GameAppId" IN (
            "#,
        );

        let mut separated = unknown_exclusions.separated(", ");
        for id in excluded_game_ids {
            separated.push_bind(*id as i64);
        }
        separated.push_unseparated(")");

        unknown_exclusions
            .build_query_scalar::<i64>()
            .fetch_all(pool)
            .await?
    };

    // Both branches GROUP BY the projected non-aggregate columns and select
    // MAX(le."BytesServed") so each unique (game, url, depot) yields one row carrying the
    // largest observed byte size for that URL — the value that sizes the probe chunk count.
    // (The no-limit branch was previously SELECT DISTINCT over the same column set; GROUP BY
    // over that exact set is equivalent for row identity while letting us aggregate bytes.)
    let mut mapped_query = if let Some(limit) = max_urls_per_game {
        eprintln!("Using sampling strategy: max {} URLs per game", limit);
        QueryBuilder::<Postgres>::new(
            "SELECT le.\"Service\", sdm.\"AppId\", COALESCE(sdm.\"AppName\", sdm.\"DepotName\", 'App ' || sdm.\"AppId\"), le.\"Url\", le.\"DepotId\", MAX(le.\"BytesServed\")
             FROM \"LogEntries\" le
             INNER JOIN \"SteamDepotMappings\" sdm ON le.\"DepotId\" = sdm.\"DepotId\"
             WHERE sdm.\"AppId\" IS NOT NULL AND le.\"Url\" IS NOT NULL AND sdm.\"IsOwner\" = true",
        )
    } else {
        QueryBuilder::<Postgres>::new(
            "SELECT le.\"Service\", sdm.\"AppId\", COALESCE(sdm.\"AppName\", sdm.\"DepotName\", 'App ' || sdm.\"AppId\"), le.\"Url\", le.\"DepotId\", MAX(le.\"BytesServed\")
             FROM \"LogEntries\" le
             INNER JOIN \"SteamDepotMappings\" sdm ON le.\"DepotId\" = sdm.\"DepotId\"
             WHERE sdm.\"AppId\" IS NOT NULL AND le.\"Url\" IS NOT NULL AND sdm.\"IsOwner\" = true",
        )
    };

    if !excluded_game_ids.is_empty() {
        mapped_query.push(" AND sdm.\"AppId\" NOT IN (");
        let mut separated = mapped_query.separated(", ");
        for id in excluded_game_ids {
            separated.push_bind(*id as i64);
        }
        separated.push_unseparated(")");
    }

    if max_urls_per_game.is_some() {
        mapped_query.push(
            " GROUP BY sdm.\"AppId\", le.\"Url\", le.\"Service\", sdm.\"AppName\", sdm.\"DepotName\", le.\"DepotId\"
              ORDER BY sdm.\"AppId\", MAX(le.\"BytesServed\") DESC",
        );
    } else {
        mapped_query.push(
            " GROUP BY le.\"Service\", sdm.\"AppId\", sdm.\"AppName\", sdm.\"DepotName\", le.\"Url\", le.\"DepotId\"
              ORDER BY sdm.\"AppId\"",
        );
    }

    let mut games_map: HashMap<u32, Vec<DownloadRecord>> = HashMap::new();
    let mut mapped_url_count = 0usize;
    let mut current_game_id: Option<u32> = None;
    let mut current_game_count = 0;

    {
        let mut rows = mapped_query.build().fetch(pool);
        while let Some(row) = rows.try_next().await? {
            let service: String = row.get(0);
            let game_app_id: i64 = row.get(1);
            let game_name: String = row.get(2);
            let url: String = row.get(3);
            let depot_id: Option<i64> = row.get(4);
            // MAX() is typed nullable; NULL means no usable size → 0 → probe-chunk floor.
            let bytes_served: Option<i64> = row.get(5);

            let record = DownloadRecord {
                service,
                game_app_id: game_app_id as u32,
                game_name,
                url,
                depot_id: depot_id.map(|d| d as u32),
                bytes_served: bytes_served.unwrap_or(0),
            };

            if let Some(limit) = max_urls_per_game {
                if Some(record.game_app_id) != current_game_id {
                    current_game_id = Some(record.game_app_id);
                    current_game_count = 0;
                }

                if current_game_count >= limit {
                    continue;
                }
                current_game_count += 1;
            }

            mapped_url_count += 1;
            games_map
                .entry(record.game_app_id)
                .or_default()
                .push(record);
        }
    }

    eprintln!("Found {} URLs across all mapped games", mapped_url_count);
    eprintln!("Querying unknown games...");

    // Both branches GROUP BY (depot, url, service) and carry MAX(le."BytesServed") as the
    // URL's size for probe sizing. (No-limit was SELECT DISTINCT over the same column set;
    // GROUP BY over that set is equivalent for row identity and lets us aggregate bytes.)
    let mut unknown_query = if max_urls_per_game.is_some() {
        QueryBuilder::<Postgres>::new(
            "SELECT le.\"Service\", le.\"DepotId\", le.\"Url\", MAX(le.\"BytesServed\")
             FROM \"LogEntries\" le
             WHERE le.\"DepotId\" IS NOT NULL
             AND le.\"Url\" IS NOT NULL
             AND NOT EXISTS (
                 SELECT 1 FROM \"SteamDepotMappings\" sdm WHERE sdm.\"DepotId\" = le.\"DepotId\"
             )",
        )
    } else {
        QueryBuilder::<Postgres>::new(
            "SELECT le.\"Service\", le.\"DepotId\", le.\"Url\", MAX(le.\"BytesServed\")
             FROM \"LogEntries\" le
             WHERE le.\"DepotId\" IS NOT NULL
             AND le.\"Url\" IS NOT NULL
             AND NOT EXISTS (
                 SELECT 1 FROM \"SteamDepotMappings\" sdm WHERE sdm.\"DepotId\" = le.\"DepotId\"
             )",
        )
    };

    if !excluded_unknown_depot_ids.is_empty() {
        unknown_query.push(" AND le.\"DepotId\" NOT IN (");
        let mut separated = unknown_query.separated(", ");
        for depot_id in &excluded_unknown_depot_ids {
            separated.push_bind(*depot_id);
        }
        separated.push_unseparated(")");
    }

    if let Some(limit) = max_urls_per_game {
        unknown_query.push(" GROUP BY le.\"DepotId\", le.\"Url\", le.\"Service\" LIMIT ");
        unknown_query.push_bind((limit * 10) as i64);
    } else {
        unknown_query.push(" GROUP BY le.\"DepotId\", le.\"Url\", le.\"Service\" ORDER BY le.\"DepotId\"");
    }

    let mut total_url_count = mapped_url_count;
    let mut unknown_current_depot: Option<u32> = None;
    let mut unknown_depot_count = 0;

    {
        let mut rows = unknown_query.build().fetch(pool);
        while let Some(row) = rows.try_next().await? {
            let service: String = row.get(0);
            let depot_id: i64 = row.get(1);
            let url: String = row.get(2);
            let bytes_served: Option<i64> = row.get(3);
            let depot_id_u32 = depot_id as u32;

            if let Some(limit) = max_urls_per_game {
                if Some(depot_id_u32) != unknown_current_depot {
                    unknown_current_depot = Some(depot_id_u32);
                    unknown_depot_count = 0;
                }

                if unknown_depot_count >= limit {
                    continue;
                }
                unknown_depot_count += 1;
            }

            total_url_count += 1;
            games_map.entry(depot_id_u32).or_default().push(DownloadRecord {
                service,
                game_app_id: depot_id_u32,
                game_name: format!("Unknown Game (Depot {})", depot_id_u32),
                url,
                depot_id: Some(depot_id_u32),
                bytes_served: bytes_served.unwrap_or(0),
            });
        }
    }

    eprintln!("Found {} total URLs to check", total_url_count);
    Ok(games_map)
}

pub(crate) async fn query_service_downloads(
    pool: &PgPool,
) -> Result<HashMap<String, Vec<(String, String, i64)>>> {
    eprintln!("Querying LogEntries for non-game services...");

    // Anti-join against all per-game mapping paths so URLs already handled per-game in
    // Phase 3 (mapped Steam depots), Phase 3b (Epic downloads), or Phase 3c (name-keyed
    // Blizzard/Riot games) are not re-probed here. Without these, the service bucket
    // re-scans every URL already matched per-game - typically millions of redundant
    // probes - AND double-counts named-game URLs as both a game and the service.
    //
    // The `dn` anti-join excludes ONLY Downloads that resolved to a named game
    // (GameName IS NOT NULL). Shared/agnostic paths with NULL GameName (e.g. shared
    // Blizzard TACT segments) legitimately stay in the service bucket - that is correct.
    //
    // GROUP BY (Service, Url) + MAX(BytesServed) replaces the old SELECT DISTINCT so each
    // (service, url) carries the URL's largest observed size for probe-chunk sizing.
    let query = "
        SELECT le.\"Service\", le.\"Url\", MAX(le.\"BytesServed\")
        FROM \"LogEntries\" le
        LEFT JOIN \"SteamDepotMappings\" sdm
            ON le.\"DepotId\" = sdm.\"DepotId\" AND sdm.\"IsOwner\" = true
        LEFT JOIN \"Downloads\" d
            ON le.\"DownloadId\" = d.\"Id\" AND d.\"EpicAppId\" IS NOT NULL
        LEFT JOIN \"Downloads\" dn
            ON le.\"DownloadId\" = dn.\"Id\"
           AND dn.\"GameAppId\" IS NULL
           AND dn.\"EpicAppId\" IS NULL
           AND dn.\"GameName\" IS NOT NULL
        WHERE le.\"Service\" IS NOT NULL
          AND le.\"Url\" IS NOT NULL
          AND LOWER(le.\"Service\") NOT IN ('unknown', 'localhost')
          AND le.\"Service\" != ''
          AND sdm.\"DepotId\" IS NULL
          AND d.\"Id\" IS NULL
          AND dn.\"Id\" IS NULL
        GROUP BY le.\"Service\", le.\"Url\"
        ORDER BY le.\"Service\"
    ";

    // Stream rows straight into the per-service map - the steam bucket alone can be millions
    // of URLs, and fetch_all would buffer every PgRow next to the map being built.
    let mut services: HashMap<String, Vec<(String, String, i64)>> = HashMap::new();

    {
        let mut rows = sqlx::query(query).fetch(pool);
        while let Some(row) = rows.try_next().await? {
            let service: String = row.get(0);
            let url: String = row.get(1);
            let bytes_served: Option<i64> = row.get(2);
            let service_lower = cache_utils::service_name_lowercase(&service);
            services
                .entry(service_lower.clone())
                .or_default()
                .push((service_lower, url, bytes_served.unwrap_or(0)));
        }
    }

    let service_count = services.len();
    let total_urls: usize = services.values().map(|v| v.len()).sum();
    eprintln!(
        "Found {} unique services with {} URLs",
        service_count,
        total_urls
    );

    Ok(services)
}

pub(crate) async fn query_epic_game_downloads(pool: &PgPool) -> Result<Vec<EpicDownloadRecord>> {
    eprintln!("Querying LogEntries for Epic game URLs...");

    // GROUP BY (Service, Url, EpicAppId, GameName) + MAX(BytesServed) replaces SELECT DISTINCT
    // so each Epic (service, url) carries the URL's largest observed size for probe-chunk sizing.
    let rows = sqlx::query(
        "SELECT le.\"Service\", le.\"Url\", d.\"EpicAppId\", d.\"GameName\", MAX(le.\"BytesServed\") AS \"MaxBytes\"
         FROM \"LogEntries\" le
         INNER JOIN \"Downloads\" d ON le.\"DownloadId\" = d.\"Id\"
         WHERE d.\"EpicAppId\" IS NOT NULL
           AND d.\"GameName\" IS NOT NULL
           AND d.\"IsEvicted\" = false
           AND le.\"Url\" IS NOT NULL
         GROUP BY le.\"Service\", le.\"Url\", d.\"EpicAppId\", d.\"GameName\"
         ORDER BY d.\"EpicAppId\"",
    )
    .fetch_all(pool)
    .await?;

    let records: Vec<EpicDownloadRecord> = rows
        .iter()
        .map(|row| EpicDownloadRecord {
            service: row.get::<String, _>("Service"),
            url: row.get::<String, _>("Url"),
            epic_app_id: row.get::<String, _>("EpicAppId"),
            game_name: row.get::<String, _>("GameName"),
            bytes_served: row.get::<Option<i64>, _>("MaxBytes").unwrap_or(0),
        })
        .collect();

    eprintln!("Found {} Epic game URLs", records.len());
    Ok(records)
}

/// Query LogEntries for name-keyed game URLs (Blizzard, Riot) - games that have a
/// GameName but neither a Steam AppId nor an Epic AppId. Identity = (Service, GameName).
/// Mirrors `query_epic_game_downloads` but gates on
/// `GameAppId IS NULL AND EpicAppId IS NULL AND GameName IS NOT NULL` instead of EpicAppId.
pub(crate) async fn query_named_game_downloads(
    pool: &PgPool,
) -> Result<Vec<NamedDownloadRecord>> {
    eprintln!("Querying LogEntries for named (Blizzard/Riot) game URLs...");

    // GROUP BY (d.Service, le.Service, Url, GameName) + MAX(BytesServed) so each named
    // (identity-service, url) carries the URL's largest observed size for probe-chunk sizing.
    //
    // IDENTITY vs CACHE-HASH split (load-bearing for Xbox):
    //   - d."Service" (aliased "Service") is the IDENTITY — it matches the column the removal
    //     binary (cache_{service}_remove) gates on (LOWER(d."Service") = $service) and is what
    //     the (service, game_name) detection key uses.
    //   - le."Service" (aliased "CacheService") is the CACHE-HASH service — cache files are hashed
    //     `(service, url)`, so file paths must be derived from the LogEntries service.
    // For Blizzard/Riot the two are equal (d.Service == le.Service at ingest). For Xbox the identity
    // is `xbox` while the cache-hash service stays `wsus` — selecting le."Service" separately is what
    // lets Xbox detection find its cache files under the wsus hash.
    let rows = sqlx::query(
        "SELECT d.\"Service\", le.\"Service\" AS \"CacheService\", le.\"Url\", d.\"GameName\", MAX(le.\"BytesServed\") AS \"MaxBytes\"
         FROM \"LogEntries\" le
         INNER JOIN \"Downloads\" d ON le.\"DownloadId\" = d.\"Id\"
         WHERE d.\"GameAppId\" IS NULL
           AND d.\"EpicAppId\" IS NULL
           AND d.\"GameName\" IS NOT NULL
           AND d.\"IsEvicted\" = false
           AND le.\"Url\" IS NOT NULL
         GROUP BY d.\"Service\", le.\"Service\", le.\"Url\", d.\"GameName\"
         ORDER BY d.\"Service\", d.\"GameName\"",
    )
    .fetch_all(pool)
    .await?;

    let records: Vec<NamedDownloadRecord> = rows
        .iter()
        .map(|row| NamedDownloadRecord {
            service: row.get::<String, _>("Service"),
            cache_service: row.get::<String, _>("CacheService"),
            url: row.get::<String, _>("Url"),
            game_name: row.get::<String, _>("GameName"),
            bytes_served: row.get::<Option<i64>, _>("MaxBytes").unwrap_or(0),
        })
        .collect();

    eprintln!("Found {} named game URLs", records.len());
    Ok(records)
}
