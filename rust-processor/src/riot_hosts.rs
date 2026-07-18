//! Riot CDN host -> game-name resolution.
//!
//! Riot bundle URLs carry NO product slug
//! (`http://<host>/channels/public/bundles/<HASH>.bundle`), so the only signal
//! that distinguishes League of Legends / Valorant / Legends of Runeterra is the
//! CDN subdomain host (the access.log `$host`, the 4th quoted field). This is the
//! Riot analogue of the Blizzard TACT-product discriminator in `tact_products.rs`.
//!
//! There are exactly three static host->game mappings (hard constants of Riot's
//! network), so this is an inline match rather than a json catalog + loader.

/// Resolve a Riot CDN host (e.g. `lol.dyn.riotcdn.net`) to its game name.
///
/// Matches case-insensitively on the subdomain prefix so regional prefixes and
/// the `.dyn.riotcdn.net` suffix are tolerated. Returns `None` for an unknown or
/// absent host (caller falls back to the generic "Riot Games" service label).
pub fn resolve_riot_host(host: &str) -> Option<&'static str> {
    let h = host.to_lowercase();
    if h.starts_with("lol.") {
        return Some("League of Legends");
    }
    if h.starts_with("valorant.") {
        return Some("Valorant");
    }
    if h.starts_with("bacon.") {
        return Some("Legends of Runeterra");
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolves_known_hosts() {
        assert_eq!(
            resolve_riot_host("lol.dyn.riotcdn.net"),
            Some("League of Legends")
        );
        assert_eq!(
            resolve_riot_host("valorant.dyn.riotcdn.net"),
            Some("Valorant")
        );
        assert_eq!(
            resolve_riot_host("bacon.dyn.riotcdn.net"),
            Some("Legends of Runeterra")
        );
    }

    #[test]
    fn is_case_insensitive() {
        assert_eq!(
            resolve_riot_host("LOL.DYN.RIOTCDN.NET"),
            Some("League of Legends")
        );
        assert_eq!(
            resolve_riot_host("Valorant.Dyn.Riotcdn.Net"),
            Some("Valorant")
        );
    }

    #[test]
    fn returns_none_for_unknown_or_absent_host() {
        assert_eq!(resolve_riot_host("unknown.dyn.riotcdn.net"), None);
        assert_eq!(resolve_riot_host(""), None);
        assert_eq!(resolve_riot_host("-"), None);
    }
}
