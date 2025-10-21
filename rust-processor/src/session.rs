use chrono::NaiveDateTime;
use std::collections::HashMap;
use std::time::Duration;

pub(crate) struct SessionTracker {
    sessions: HashMap<String, NaiveDateTime>,
    gap_timeout: Duration,
    cleanup_counter: usize,
}

impl SessionTracker {
    pub(crate) fn new(gap_timeout: Duration) -> Self {
        Self {
            sessions: HashMap::new(),
            gap_timeout,
            cleanup_counter: 0,
        }
    }

    pub(crate) fn should_create_new_session(
        &self,
        session_key: &str,
        current_timestamp: NaiveDateTime,
    ) -> bool {
        if let Some(&last_activity) = self.sessions.get(session_key) {
            let duration = current_timestamp.signed_duration_since(last_activity);
            duration.num_seconds() > self.gap_timeout.as_secs() as i64
        } else {
            true
        }
    }

    pub(crate) fn update_session(&mut self, session_key: &str, timestamp: NaiveDateTime) {
        self.sessions.insert(session_key.to_string(), timestamp);

        // Perform cleanup every 1000 updates to prevent unbounded growth
        self.cleanup_counter += 1;
        if self.cleanup_counter >= 1000 {
            self.cleanup_old_sessions(timestamp);
            self.cleanup_counter = 0;
        }
    }

    /// Remove sessions that are older than 2x the gap timeout
    /// This prevents the HashMap from growing indefinitely while keeping active sessions
    fn cleanup_old_sessions(&mut self, current_timestamp: NaiveDateTime) {
        let cleanup_threshold = self.gap_timeout.as_secs() * 2;

        // Retain only sessions that were active within 2x the gap timeout
        self.sessions.retain(|_, &mut last_activity| {
            let duration = current_timestamp.signed_duration_since(last_activity);
            duration.num_seconds() <= cleanup_threshold as i64
        });
    }
}