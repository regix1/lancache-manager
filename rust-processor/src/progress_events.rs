//! Progress event module for emitting JSON progress events to stdout.
//!
//! This module provides a standardized way to emit progress events that can be
//! consumed by the C# host application via stdout capture.
//!
//! Event format:
//! - Start: {"event":"started","operationId":"<uuid>","status":"running","stageKey":"...","context":{...}}
//! - Progress: {"event":"progress","operationId":"<uuid>","percentComplete":<0-100>,"status":"running","stageKey":"...","context":{...}}
//! - Complete: {"event":"complete","operationId":"<uuid>","success":true/false,"status":"completed/failed","stageKey":"...","context":{...},"cancelled":false,"errorDetail":null|"<full anyhow chain>"}
//!
//! `errorDetail` is ALWAYS present on the complete/failed/cancelled envelope: it is
//! `null` on success and cancel, and carries the full `anyhow` context chain
//! (`format!("{e:#}")`) on failure. The C# host reads it into its failure path.

#![allow(dead_code)]

use std::io::Write;

use serde::Serialize;
use uuid::Uuid;

/// Progress event reporter that emits JSON lines to stdout
pub struct ProgressReporter {
    operation_id: String,
    enabled: bool,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct StartEvent {
    event: &'static str,
    #[serde(rename = "operationId")]
    operation_id: String,
    status: &'static str,
    stage_key: String,
    context: serde_json::Value,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ProgressEvent {
    event: &'static str,
    #[serde(rename = "operationId")]
    operation_id: String,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    status: &'static str,
    stage_key: String,
    context: serde_json::Value,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CompleteEvent {
    event: &'static str,
    #[serde(rename = "operationId")]
    operation_id: String,
    success: bool,
    status: &'static str,
    stage_key: String,
    context: serde_json::Value,
    cancelled: bool,
    /// Full `anyhow` error chain (`format!("{e:#}")`) for the failed terminal.
    /// ALWAYS present in the envelope: `null` on success/cancel, a string on failure.
    /// Intentionally NO `skip_serializing_if`, so the C# host can always read the field.
    #[serde(rename = "errorDetail")]
    error_detail: Option<String>,
}

impl ProgressReporter {
    /// Create a new progress reporter
    ///
    /// # Arguments
    /// * `enabled` - Whether progress reporting is enabled (based on --progress flag)
    pub fn new(enabled: bool) -> Self {
        Self {
            operation_id: Uuid::new_v4().to_string(),
            enabled,
        }
    }

    /// Get the operation ID
    pub fn operation_id(&self) -> &str {
        &self.operation_id
    }

    /// Check if progress reporting is enabled
    pub fn is_enabled(&self) -> bool {
        self.enabled
    }

    /// Emit a started event
    pub fn emit_started(&self, stage_key: &str, context: serde_json::Value) {
        if !self.enabled {
            return;
        }

        let event = StartEvent {
            event: "started",
            operation_id: self.operation_id.clone(),
            status: "running",
            stage_key: stage_key.to_string(),
            context,
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
            let _ = std::io::stdout().flush();
        }
    }

    /// Emit a progress event
    ///
    /// # Arguments
    /// * `percent_complete` - Progress percentage (0-100)
    /// * `stage_key` - Semantic i18n stage key
    /// * `context` - Interpolation variables as JSON object
    pub fn emit_progress(&self, percent_complete: f64, stage_key: &str, context: serde_json::Value) {
        if !self.enabled {
            return;
        }

        let event = ProgressEvent {
            event: "progress",
            operation_id: self.operation_id.clone(),
            percent_complete: percent_complete.clamp(0.0, 100.0),
            status: "running",
            stage_key: stage_key.to_string(),
            context,
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
            let _ = std::io::stdout().flush();
        }
    }

    /// Emit a complete event (success)
    ///
    /// # Arguments
    /// * `stage_key` - Semantic i18n stage key
    /// * `context` - Interpolation variables as JSON object
    pub fn emit_complete(&self, stage_key: &str, context: serde_json::Value) {
        if !self.enabled {
            return;
        }

        let event = CompleteEvent {
            event: "complete",
            operation_id: self.operation_id.clone(),
            success: true,
            status: "completed",
            stage_key: stage_key.to_string(),
            context,
            cancelled: false,
            error_detail: None,
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
            let _ = std::io::stdout().flush();
        }
    }

    /// Emit a complete event (failure)
    ///
    /// # Arguments
    /// * `stage_key` - Semantic i18n stage key
    /// * `context` - Interpolation variables as JSON object
    /// * `error_detail` - The full `anyhow` error chain (`format!("{e:#}")`), surfaced
    ///   as the always-present top-level `errorDetail` envelope field. Pass `None` only
    ///   when there is genuinely no error object to attach; prefer the shared
    ///   [`run_or_exit`] / [`finish_or_exit`] helpers, which populate it from the
    ///   propagated `anyhow::Error` for you.
    pub fn emit_failed(
        &self,
        stage_key: &str,
        context: serde_json::Value,
        error_detail: Option<String>,
    ) {
        if !self.enabled {
            return;
        }

        let event = CompleteEvent {
            event: "complete",
            operation_id: self.operation_id.clone(),
            success: false,
            status: "failed",
            stage_key: stage_key.to_string(),
            context,
            cancelled: false,
            error_detail,
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
            let _ = std::io::stdout().flush();
        }
    }

    /// Emit a complete event (cancelled)
    ///
    /// # Arguments
    /// * `stage_key` - Semantic i18n stage key
    /// * `context` - Interpolation variables as JSON object
    pub fn emit_cancelled(&self, stage_key: &str, context: serde_json::Value) {
        if !self.enabled {
            return;
        }

        let event = CompleteEvent {
            event: "complete",
            operation_id: self.operation_id.clone(),
            success: false,
            status: "cancelled",
            stage_key: stage_key.to_string(),
            context,
            cancelled: true,
            // Cancellation is a distinct terminal, NOT a failure: no error chain.
            error_detail: None,
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
            let _ = std::io::stdout().flush();
        }
    }
}

/// Run a binary's whole body and turn any propagated error into the crate's ONE
/// uniform failure terminal.
///
/// Wrap the body of `fn main` in this so that an error bubbled via `?` produces the
/// SAME structured stdout `failed` event that hand-written catch sites do — instead
/// of anyhow's default (stderr + exit 1 with no stdout envelope, which the C# host
/// cannot read as a structured terminal).
///
/// Behavior:
/// - `Ok(())` → returns normally. The binary keeps ownership of its success terminal:
///   call your own [`ProgressReporter::emit_complete`] (with the right stage key +
///   context) inside the closure or after this call, then let `main` return `Ok(())`
///   so the process exits `0`.
/// - `Err(e)` → routes through [`finish_or_exit`]: emits
///   `emit_failed(fail_stage_key, {}, Some(format!("{e:#}")))` (the full anyhow context
///   chain, gated by `--progress` like every envelope), ALWAYS `eprintln!`s the same
///   chain to stderr (so callers running WITHOUT `--progress` still get a reason), then
///   `std::process::exit(1)`. This function does NOT return on the error path.
///
/// Cancellation is NOT failure: a cooperative cancel returns `Ok(())` from the closure
/// after calling [`ProgressReporter::emit_cancelled`], never an `Err`.
///
/// # Sync `fn main`
/// ```ignore
/// fn main() -> anyhow::Result<()> {
///     let reporter = ProgressReporter::new(progress_enabled);
///     progress_events::run_or_exit(&reporter, "cache.clear.failed", || {
///         do_work(&reporter)?;                       // any `?` routes to the failed terminal
///         reporter.emit_complete("cache.clear.completed", serde_json::json!({ /* ... */ }));
///         Ok(())
///     });
///     Ok(())
/// }
/// ```
///
/// # Async `#[tokio::main]` main
/// A sync closure cannot `.await`; compute the `Result` first, then hand it to
/// [`finish_or_exit`] (the same funnel):
/// ```ignore
/// #[tokio::main]
/// async fn main() -> anyhow::Result<()> {
///     let reporter = ProgressReporter::new(progress_enabled);
///     let result = run_inner(&reporter).await;       // async body returning anyhow::Result<()>
///     progress_events::finish_or_exit(&reporter, "op.failed", result);
///     Ok(())
/// }
/// ```
pub fn run_or_exit<F: FnOnce() -> anyhow::Result<()>>(
    reporter: &ProgressReporter,
    fail_stage_key: &str,
    f: F,
) {
    finish_or_exit(reporter, fail_stage_key, f());
}

/// The core of [`run_or_exit`] for callers that already hold an `anyhow::Result<()>`
/// (e.g. an async `#[tokio::main]` body that had to `.await` before it could produce
/// one).
///
/// On `Ok(())` returns normally. On `Err(e)` this is the SINGLE place that produces the
/// crate's uniform failure terminal: it emits the structured `failed` envelope with the
/// full anyhow chain as `errorDetail` (gated by `--progress`), ALWAYS writes that chain
/// to stderr, then exits the process with code `1` (never returns).
pub fn finish_or_exit(
    reporter: &ProgressReporter,
    fail_stage_key: &str,
    result: anyhow::Result<()>,
) {
    if let Err(e) = result {
        // Full anyhow context chain, e.g. "outer context: inner: root cause".
        let detail = format!("{e:#}");
        // Structured stdout terminal (gated by --progress inside emit_failed).
        reporter.emit_failed(fail_stage_key, serde_json::json!({}), Some(detail.clone()));
        // Human log on stderr ALWAYS, even when --progress is off, so a no-progress
        // caller still gets a reason instead of a bare exit code.
        eprintln!("{detail}");
        std::process::exit(1);
    }
}

/// Serialize any value to compact JSON, print it as a single NDJSON line, and flush
/// stdout — the same write/flush guarantee every `ProgressReporter` emit method above
/// gives its envelope events.
///
/// This is a bare emission primitive: it does NOT wrap `value` in the
/// started/progress/complete envelope those methods use. It exists for callers with
/// their own continuous wire shape (e.g. `speed_tracker`'s `DownloadSpeedSnapshot`
/// stream) whose consumer parses the line directly, so wrapping it here would change
/// the wire format. Use `ProgressReporter`'s methods instead when emitting a tracked
/// started/progress/complete/failed/cancelled operation.
pub fn emit_json_line<T: Serialize>(value: &T) {
    if let Ok(json) = serde_json::to_string(value) {
        println!("{}", json);
        let _ = std::io::stdout().flush();
    }
}
