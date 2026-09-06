/// Cooperative cancellation for Rust binaries via stdin protocol.
///
/// C# writes `CANCEL\n` to the child's stdin (or closes it) to request cooperative stop.
/// This module installs a background thread that watches for that line and sets a global
/// AtomicBool. Long loops check `is_cancelled()` between items and exit cleanly (code 0).
///
/// Usage:
///   1. Call `cancel::install()` once at the top of `main()`.
///   2. Poll `cancel::is_cancelled()` between loop iterations; finish the in-flight item,
///      flush a partial progress event with real counts, then return / exit 0.
use std::io::BufRead;
use std::sync::atomic::{AtomicBool, Ordering};

static CANCELLED: AtomicBool = AtomicBool::new(false);

/// Spawn a detached background thread that watches stdin for an explicit "CANCEL" line
/// and sets the global cancellation flag.  Call once at the start of `main()`.
/// Safe for both sync and `#[tokio::main]` binaries (plain OS thread, no runtime needed).
///
/// IMPORTANT: cancellation is set ONLY on an explicit trimmed "CANCEL" line. EOF (stdin
/// closed/inherited-closed) and read errors just stop watching WITHOUT cancelling, so a
/// binary spawned without a piped stdin (or whose parent closes stdin) does not self-cancel.
pub fn install() {
    std::thread::spawn(|| {
        let stdin = std::io::stdin();
        let mut line = String::new();
        loop {
            line.clear();
            match stdin.lock().read_line(&mut line) {
                Ok(0) => {
                    // EOF: parent closed/never piped stdin — stop watching, do NOT cancel.
                    break;
                }
                Ok(_) => {
                    if line.trim().eq_ignore_ascii_case("CANCEL") {
                        CANCELLED.store(true, Ordering::SeqCst);
                        break;
                    }
                }
                Err(_) => {
                    // Broken pipe or other read error — stop watching, do NOT cancel.
                    break;
                }
            }
        }
    });
}

/// Returns `true` if a cancellation has been requested via stdin.
/// Cheap: single relaxed atomic load, safe to call in tight loops.
#[inline]
pub fn is_cancelled() -> bool {
    CANCELLED.load(Ordering::Relaxed)
}
