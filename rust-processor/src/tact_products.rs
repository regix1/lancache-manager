/// Blizzard / Battle.net TACT product / CDN-path -> game display name mapping.
///
/// Blizzard lancache traffic arrives with `Service = "blizzard"` (resolved by
/// nginx upstream config), but unlike Steam (depot id) or Epic (CDN path) there
/// is no integer app id. Instead the CDN request path carries a product/CDN-path
/// segment after `/tpr/` (e.g. `/tpr/wow/data/...`, `/tpr/configs/...`).
///
/// The catalog is single-sourced from `tact_products.json` (sibling of this file)
/// and embedded at compile time via `include_str!`. The SAME JSON is read by the
/// C# `BattleNetMappingService` (embedded resource) so the two backends never
/// drift. Do NOT duplicate the catalog data anywhere else.
///
/// Resolution for a `/tpr/<seg>/` segment (lowercased):
///   1. `products[seg]`  -> game display name
///   2. `aliases[seg]`   -> game display name (extension point for CDN paths
///                          that diverge from the Ribbit product slug)
///   3. `seg ∈ shared`   -> the shared label ("Battle.net (shared)") for
///                          product-agnostic paths (configs/agent/catalogs/...)
///   4. otherwise        -> unresolved (caller leaves `GameName` NULL)
///
/// There is no integer app id, so `Downloads.GameAppId`/`DepotId` stay NULL for
/// Blizzard rows.
use std::collections::{HashMap, HashSet};
use std::sync::OnceLock;

use serde::Deserialize;

/// Embedded single-source catalog JSON (sibling `tact_products.json`).
const TACT_PRODUCTS_JSON: &str = include_str!("../tact_products.json");

/// Raw deserialization shape matching `tact_products.json`.
#[derive(Deserialize)]
struct RawCatalog {
    #[serde(rename = "sharedLabel")]
    shared_label: String,
    products: HashMap<String, String>,
    #[serde(default)]
    aliases: HashMap<String, String>,
    #[serde(default)]
    shared: Vec<String>,
}

/// Parsed, lowercase-normalized catalog used for lookups.
struct Catalog {
    shared_label: String,
    /// Merged product + alias map (lowercased keys) -> game display name.
    /// Products take precedence over aliases on key collision.
    games: HashMap<String, String>,
    /// Lowercased product-agnostic shared segments.
    shared: HashSet<String>,
}

/// The resolution outcome for a `/tpr/<seg>/` segment.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum TactResolution {
    /// Resolved to a concrete game display name (products or aliases).
    Game(String),
    /// A product-agnostic shared path (configs/agent/catalogs/...) -> shared label.
    Shared(String),
    /// Unknown segment - caller leaves the row generic / GameName NULL.
    Unknown,
}

/// Parse the embedded JSON exactly once (lazy, thread-safe).
fn catalog() -> &'static Catalog {
    static CATALOG: OnceLock<Catalog> = OnceLock::new();
    CATALOG.get_or_init(|| {
        let raw: RawCatalog = serde_json::from_str(TACT_PRODUCTS_JSON)
            .expect("embedded tact_products.json is malformed");

        // Aliases first, then products, so a product slug always wins on collision.
        let mut games: HashMap<String, String> = HashMap::new();
        for (code, name) in raw.aliases {
            games.insert(code.to_lowercase(), name);
        }
        for (code, name) in raw.products {
            games.insert(code.to_lowercase(), name);
        }

        let shared: HashSet<String> = raw.shared.into_iter().map(|s| s.to_lowercase()).collect();

        Catalog {
            shared_label: raw.shared_label,
            games,
            shared,
        }
    })
}

/// Parse the TACT CDN-path / product segment from a Blizzard CDN request URL.
///
/// Most products publish a `tpr/<seg>/...` CDN path where `<seg>` is the TACT product
/// code (or a path alias). A few publish a path with NO `tpr/` prefix: verified against
/// the live `/cdns` configs, `btlr` is `cortez/Cerberus-B-Live` while every other
/// catalog product is `tpr/<code>`. When `tpr` is absent the FIRST path segment is the
/// CDN path root, and that fallback is accepted ONLY when the catalog resolves it, so
/// arbitrary Blizzard-vhost paths keep returning `None` instead of inventing segments.
///
/// Unlike the old implementation this does NOT special-case `configs`: the shared
/// segments are now resolved (to the shared label) rather than dropped, so the
/// segment must be returned for the resolver to classify it.
#[allow(dead_code)] // Only used by the log_processor binary, not the cache_* binaries that share parser.rs
pub(crate) fn extract_tact_product(url: &str) -> Option<String> {
    let segments: Vec<&str> = url.split('/').filter(|s| !s.is_empty()).collect();
    // Find the "tpr" segment and take the following segment as the CDN path / product code.
    if let Some(tpr_idx) = segments.iter().position(|&s| s == "tpr") {
        let product = segments.get(tpr_idx + 1)?;
        if product.is_empty() {
            return None;
        }
        return Some(product.to_lowercase());
    }

    let first = segments.first()?.to_lowercase();
    match resolve_tact_segment(&first) {
        TactResolution::Unknown => None,
        _ => Some(first),
    }
}

/// Resolve a TACT CDN-path / product segment to a game name, shared label, or unknown.
///
/// Returns `TactResolution::Unknown` for genuinely unrecognized segments (no
/// fallback default), mirroring how an unmapped Steam depot leaves `GameName` NULL.
#[allow(dead_code)] // Only used by the log_processor binary, not the cache_* binaries that share parser.rs
pub(crate) fn resolve_tact_segment(segment: &str) -> TactResolution {
    let seg = segment.to_lowercase();
    let cat = catalog();

    if let Some(name) = cat.games.get(&seg) {
        return TactResolution::Game(name.clone());
    }
    if cat.shared.contains(&seg) {
        return TactResolution::Shared(cat.shared_label.clone());
    }
    TactResolution::Unknown
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn extract_tact_product_falls_back_to_catalog_known_path_root() {
        // btlr publishes a non-tpr CDN path (cortez/Cerberus-B-Live).
        assert_eq!(
            extract_tact_product("/cortez/Cerberus-B-Live/data/81/1d/811de194873df83c").as_deref(),
            Some("cortez")
        );
        assert_eq!(
            resolve_tact_segment("cortez"),
            TactResolution::Game("Call of Duty: Black Ops 6".to_string())
        );
    }

    #[test]
    fn extract_tact_product_rejects_unknown_path_roots() {
        // Arbitrary Blizzard-vhost paths must not invent product segments.
        assert_eq!(extract_tact_product("/filestreamingservice/files/abc"), None);
        assert_eq!(extract_tact_product("/Cerberus-B-Live/data/81/1d/abc"), None);
        assert_eq!(extract_tact_product("/"), None);
    }

    #[test]
    fn extract_tact_product_parses_product_segment() {
        assert_eq!(
            extract_tact_product("/tpr/wow/data/ab/cd/abcdef").as_deref(),
            Some("wow")
        );
        assert_eq!(
            extract_tact_product("/tpr/fenris/patch/00/11/0011aa").as_deref(),
            Some("fenris")
        );
    }

    #[test]
    fn extract_tact_product_lowercases() {
        assert_eq!(
            extract_tact_product("/tpr/WoW/data/ab").as_deref(),
            Some("wow")
        );
    }

    #[test]
    fn extract_tact_product_returns_shared_segment() {
        // configs is now returned (it is classified as shared by the resolver),
        // no longer dropped at extraction time.
        assert_eq!(
            extract_tact_product("/tpr/configs/data/ab/cd/ef").as_deref(),
            Some("configs")
        );
    }

    #[test]
    fn extract_tact_product_none_when_no_tpr() {
        assert_eq!(extract_tact_product("/depot/123456/chunk/abcdef"), None);
        assert_eq!(extract_tact_product("/tpr"), None);
        assert_eq!(extract_tact_product("/tpr/"), None);
    }

    #[test]
    fn resolve_known_products() {
        assert_eq!(
            resolve_tact_segment("wow"),
            TactResolution::Game("World of Warcraft".to_string())
        );
        assert_eq!(
            resolve_tact_segment("fenris"),
            TactResolution::Game("Diablo IV".to_string())
        );
        assert_eq!(
            resolve_tact_segment("pro"),
            TactResolution::Game("Overwatch".to_string())
        );
        // case-insensitive
        assert_eq!(
            resolve_tact_segment("S2"),
            TactResolution::Game("StarCraft II".to_string())
        );
        assert_eq!(
            resolve_tact_segment("odin"),
            TactResolution::Game("Call of Duty: Modern Warfare (2019)".to_string())
        );
        assert_eq!(
            resolve_tact_segment("auks"),
            TactResolution::Game("Call of Duty".to_string())
        );
    }

    #[test]
    fn resolve_shared_segments() {
        assert_eq!(
            resolve_tact_segment("configs"),
            TactResolution::Shared("Battle.net (shared)".to_string())
        );
        assert_eq!(
            resolve_tact_segment("agent"),
            TactResolution::Shared("Battle.net (shared)".to_string())
        );
        assert_eq!(
            resolve_tact_segment("catalogs"),
            TactResolution::Shared("Battle.net (shared)".to_string())
        );
    }

    #[test]
    fn resolve_unknown_returns_unknown() {
        assert_eq!(
            resolve_tact_segment("notarealcode"),
            TactResolution::Unknown
        );
    }
}
