//! Progress event module for emitting JSON progress events to stdout.
//!
//! This module provides a standardized way to emit progress events that can be
//! consumed by the C# host application via stdout capture.
//!
//! Event format:
//! - Start: {"event":"started","operationId":"<uuid>","status":"running","stageKey":"...","context":{...}}
//! - Progress: {"event":"progress","operationId":"<uuid>","percentComplete":<0-100>,"status":"running","stageKey":"...","context":{...}}
//! - Complete: {"event":"complete","operationId":"<uuid>","success":true/false,"status":"completed/failed","stageKey":"...","context":{...},"cancelled":false}

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
    pub fn emit_failed(&self, stage_key: &str, context: serde_json::Value) {
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
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
            let _ = std::io::stdout().flush();
        }
    }
}
