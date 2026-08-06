//! Riot name-keyed game cache removal bin.
//!
//! Thin wrapper over the shared name-keyed removal core: it pins the owning service
//! to "riot" and delegates the entire removal flow to `named_remove_core::run`.
//! All logic lives in `named_remove_core` (head) + `removal_core` (shared tail).

use anyhow::Result;

use lancache_processor::cancel;
use lancache_processor::named_remove_core;
#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();
    named_remove_core::run("riot").await
}
