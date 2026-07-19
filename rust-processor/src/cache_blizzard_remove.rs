//! Blizzard name-keyed game cache removal bin.
//!
//! Thin wrapper over the shared name-keyed removal core: it pins the owning service
//! to "blizzard" and delegates the entire removal flow to `named_remove_core::run`.
//! All logic lives in `named_remove_core` (head) + `removal_core` (shared tail).

use anyhow::Result;

mod cache_utils;
mod cancel;
mod db;
mod log_discovery;
mod log_layout;
mod log_reader;
mod log_purge;
mod models;
mod named_remove_core;
mod parser;
mod parser_http_detailed;
mod progress_events;
mod progress_utils;
mod removal_core;
#[cfg(test)]
mod riot_hosts;
mod service_utils;
mod tact_products;

#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();
    named_remove_core::run("blizzard").await
}
