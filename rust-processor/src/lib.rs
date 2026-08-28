//! Shared code for the lancache binaries.
//!
//! These modules used to be pulled into each binary with `mod`, which compiled a separate
//! copy per binary and made Rust report anything one binary did not call as dead code. Owning
//! them here means the analysis runs once, against the whole surface.

pub mod cache_corruption_detector;
pub mod cache_structural_scanner;
pub mod cache_structural_state;
pub mod cache_utils;
pub mod cancel;
pub mod content_scan;
pub mod db;
pub mod log_discovery;
pub mod log_layout;
pub mod log_purge;
pub mod log_reader;
pub mod models;
pub mod named_remove_core;
pub mod parser;
pub mod parser_http_detailed;
pub mod progress_events;
pub mod progress_utils;
pub mod removal_core;
pub mod riot_hosts;
pub mod service_utils;
pub mod session;
pub mod tact_products;
