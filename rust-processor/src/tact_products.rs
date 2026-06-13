/// Blizzard / Battle.net TACT product code -> game display name mapping.
///
/// Blizzard lancache traffic arrives with `Service = "blizzard"` (resolved by
/// nginx upstream config), but unlike Steam (depot id) or Epic (CDN path) there
/// is no integer app id. Instead the TACT product code is embedded in the CDN
/// request path as the segment after `/tpr/` (e.g. `/tpr/wow/data/...`).
///
/// This module parses that product code from the URL and looks it up in a
/// static table to produce a human-readable game name. There is no integer app
/// id, so `Downloads.GameAppId`/`DepotId` stay NULL for Blizzard rows.

/// Static TACT product code -> display name table (Blizzard / Activision /
/// Microsoft products served over Blizzard's TACT CDN).
const TACT_PRODUCTS: &[(&str, &str)] = &[
    // Blizzard
    ("wow", "World of Warcraft"),
    ("fenris", "Diablo IV"),
    ("pro", "Overwatch 2"),
    ("hsb", "Hearthstone"),
    ("d3", "Diablo III"),
    ("osi", "Diablo II: Resurrected"),
    ("anbs", "Diablo Immortal"),
    ("hero", "Heroes of the Storm"),
    ("s1", "StarCraft: Remastered"),
    ("s2", "StarCraft II"),
    ("w3", "Warcraft III: Reforged"),
    ("w1r", "Warcraft I: Remastered"),
    ("w2r", "Warcraft II: Remastered"),
    ("rtro", "Blizzard Arcade Collection"),
    // Activision
    ("viper", "Call of Duty: Black Ops 4"),
    ("zeus", "Call of Duty: Black Ops Cold War"),
    ("odin", "Call of Duty: Modern Warfare (2019)"),
    ("auks", "Call of Duty"),
    ("lazr", "Call of Duty: Modern Warfare 2 Remastered"),
    ("fore", "Call of Duty: Vanguard"),
    ("wlby", "Crash Bandicoot 4"),
    // Microsoft
    ("aqua", "Avowed"),
    ("scor", "Sea of Thieves"),
];

/// Parse the TACT product code from a Blizzard CDN request URL.
///
/// Blizzard lancache URLs follow the pattern `/tpr/<product>/...` where
/// `<product>` is the TACT product code. Returns the lowercased product code,
/// or `None` if the URL does not contain a `/tpr/<segment>/` path.
///
/// The `configs` segment (`/tpr/configs/...`) is a shared, product-agnostic
/// config path and is intentionally not treated as a product code.
#[allow(dead_code)] // Only used by the log_processor binary, not the cache_* binaries that share parser.rs
pub(crate) fn extract_tact_product(url: &str) -> Option<String> {
    let segments: Vec<&str> = url.split('/').filter(|s| !s.is_empty()).collect();
    // Find the "tpr" segment and take the following segment as the product code.
    let tpr_idx = segments.iter().position(|&s| s == "tpr")?;
    let product = segments.get(tpr_idx + 1)?;
    if product.is_empty() || *product == "configs" {
        return None;
    }
    Some(product.to_lowercase())
}

/// Look up the display name for a TACT product code.
///
/// Returns `None` for unknown product codes (no fallback default), mirroring how
/// an unmapped Steam depot leaves `GameName` NULL.
#[allow(dead_code)] // Only used by the log_processor binary, not the cache_* binaries that share parser.rs
pub(crate) fn tact_display_name(code: &str) -> Option<&'static str> {
    let code_lower = code.to_lowercase();
    TACT_PRODUCTS
        .iter()
        .find(|(c, _)| *c == code_lower)
        .map(|(_, name)| *name)
}

#[cfg(test)]
mod tests {
    use super::*;

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
    fn extract_tact_product_skips_configs() {
        assert_eq!(extract_tact_product("/tpr/configs/data/ab/cd/ef"), None);
    }

    #[test]
    fn extract_tact_product_none_when_no_tpr() {
        assert_eq!(extract_tact_product("/depot/123456/chunk/abcdef"), None);
        assert_eq!(extract_tact_product("/tpr"), None);
        assert_eq!(extract_tact_product("/tpr/"), None);
    }

    #[test]
    fn tact_display_name_known_codes() {
        assert_eq!(tact_display_name("wow"), Some("World of Warcraft"));
        assert_eq!(tact_display_name("fenris"), Some("Diablo IV"));
        assert_eq!(tact_display_name("pro"), Some("Overwatch 2"));
        assert_eq!(tact_display_name("S2"), Some("StarCraft II"));
    }

    #[test]
    fn tact_display_name_unknown_returns_none() {
        assert_eq!(tact_display_name("notarealcode"), None);
    }
}
