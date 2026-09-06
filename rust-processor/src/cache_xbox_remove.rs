//! Xbox name-keyed game cache removal bin.
//!
//! Thin wrapper over the shared name-keyed removal core: it pins the owning service
//! to "xbox" and delegates the entire removal flow to `named_remove_core::run`.
//! All logic lives in `named_remove_core` (head) + `removal_core` (shared tail).
//!
//! Xbox identity is `Downloads.Service='xbox'` while its cache slices are hashed under
//! `LogEntries.Service='wsus'`. The core handles that split by gating identity on the Download
//! side and never constraining `le.Service`: each log row carries its own service through to the
//! cache delete and the log purge, so an `xbox` identity removes `wsus`-hashed files. Constraining
//! `le.Service` anywhere in that path returns zero rows for Xbox and leaves cache and log behind,
//! which is what `url_query_does_not_constrain_log_entry_service` in the core exists to catch.

use anyhow::Result;

use lancache_processor::cancel;
use lancache_processor::named_remove_core;
#[tokio::main]
async fn main() -> Result<()> {
    cancel::install();
    named_remove_core::run("xbox").await
}
