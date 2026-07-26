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

use std::collections::HashSet;

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

/// Per-process telemetry for actual Riot host observations. Counts are unique by normalized host so
/// repeated bundle requests do not fabricate mapping work.
#[derive(Default)]
#[allow(dead_code)]
pub struct RiotMappingCounters {
    processed_hosts: HashSet<String>,
    mapped_hosts: HashSet<String>,
}

#[allow(dead_code)]
pub struct RiotHostObservation {
    pub game_name: Option<&'static str>,
    pub first_observation: bool,
}

#[allow(dead_code)]
impl RiotMappingCounters {
    pub fn observe(&mut self, host: &str) -> RiotHostObservation {
        let normalized = host.trim().to_lowercase();
        if normalized.is_empty() || normalized == "-" {
            return RiotHostObservation {
                game_name: None,
                first_observation: false,
            };
        }

        let game_name = resolve_riot_host(&normalized);
        let first_observation = self.processed_hosts.insert(normalized.clone());
        if first_observation && game_name.is_some() {
            self.mapped_hosts.insert(normalized);
        }

        RiotHostObservation {
            game_name,
            first_observation,
        }
    }

    pub fn processed(&self) -> u64 {
        self.processed_hosts.len() as u64
    }

    pub fn mapped(&self) -> u64 {
        self.mapped_hosts.len() as u64
    }
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

    #[test]
    fn counters_track_unique_known_and_unknown_hosts() {
        let mut counters = RiotMappingCounters::default();

        let known = counters.observe("LOL.DYN.RIOTCDN.NET");
        assert_eq!(known.game_name, Some("League of Legends"));
        assert!(known.first_observation);

        let duplicate = counters.observe("lol.dyn.riotcdn.net");
        assert_eq!(duplicate.game_name, Some("League of Legends"));
        assert!(!duplicate.first_observation);

        let unknown = counters.observe("future.dyn.riotcdn.net");
        assert_eq!(unknown.game_name, None);
        assert!(unknown.first_observation);

        counters.observe("");
        counters.observe("-");

        assert_eq!(counters.processed(), 2);
        assert_eq!(counters.mapped(), 1);
    }
}
