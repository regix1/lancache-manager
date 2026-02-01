//! Progress event module for emitting JSON progress events to stdout.
//!
//! This module provides a standardized way to emit progress events that can be
//! consumed by the C# host application via stdout capture.
//!
//! Event format:
//! - Start: {"event":"started","operationId":"<uuid>","status":"running"}
//! - Progress: {"event":"progress","operationId":"<uuid>","percentComplete":<0-100>,"status":"running","message":"..."}
//! - Complete: {"event":"complete","operationId":"<uuid>","success":true/false,"status":"completed/failed","message":"...","cancelled":false}

#![allow(dead_code)]

use serde::Serialize;
use uuid::Uuid;

/// Progress event reporter that emits JSON lines to stdout
pub struct ProgressReporter {
    operation_id: String,
    enabled: bool,
}

#[derive(Serialize)]
struct StartEvent {
    event: &'static str,
    #[serde(rename = "operationId")]
    operation_id: String,
    status: &'static str,
}

#[derive(Serialize)]
struct ProgressEvent {
    event: &'static str,
    #[serde(rename = "operationId")]
    operation_id: String,
    #[serde(rename = "percentComplete")]
    percent_complete: f64,
    status: &'static str,
    message: String,
}

#[derive(Serialize)]
struct CompleteEvent {
    event: &'static str,
    #[serde(rename = "operationId")]
    operation_id: String,
    success: bool,
    status: &'static str,
    message: String,
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
    pub fn emit_started(&self) {
        if !self.enabled {
            return;
        }

        let event = StartEvent {
            event: "started",
            operation_id: self.operation_id.clone(),
            status: "running",
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
        }
    }

    /// Emit a progress event
    ///
    /// # Arguments
    /// * `percent_complete` - Progress percentage (0-100)
    /// * `message` - Human-readable progress message
    pub fn emit_progress(&self, percent_complete: f64, message: &str) {
        if !self.enabled {
            return;
        }

        let event = ProgressEvent {
            event: "progress",
            operation_id: self.operation_id.clone(),
            percent_complete: percent_complete.clamp(0.0, 100.0),
            status: "running",
            message: message.to_string(),
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
        }
    }

    /// Emit a complete event (success)
    ///
    /// # Arguments
    /// * `message` - Completion message
    pub fn emit_complete(&self, message: &str) {
        if !self.enabled {
            return;
        }

        let event = CompleteEvent {
            event: "complete",
            operation_id: self.operation_id.clone(),
            success: true,
            status: "completed",
            message: message.to_string(),
            cancelled: false,
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
        }
    }

    /// Emit a complete event (failure)
    ///
    /// # Arguments
    /// * `message` - Error message
    pub fn emit_failed(&self, message: &str) {
        if !self.enabled {
            return;
        }

        let event = CompleteEvent {
            event: "complete",
            operation_id: self.operation_id.clone(),
            success: false,
            status: "failed",
            message: message.to_string(),
            cancelled: false,
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
        }
    }

    /// Emit a complete event (cancelled)
    ///
    /// # Arguments
    /// * `message` - Cancellation message
    pub fn emit_cancelled(&self, message: &str) {
        if !self.enabled {
            return;
        }

        let event = CompleteEvent {
            event: "complete",
            operation_id: self.operation_id.clone(),
            success: false,
            status: "cancelled",
            message: message.to_string(),
            cancelled: true,
        };

        if let Ok(json) = serde_json::to_string(&event) {
            println!("{}", json);
        }
    }
}
