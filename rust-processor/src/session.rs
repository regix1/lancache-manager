use chrono::NaiveDateTime;
use std::collections::HashMap;
use std::time::Duration;

pub struct SessionTracker {
    sessions: HashMap<String, NaiveDateTime>,
    gap_timeout: Duration,
}

impl SessionTracker {
    pub fn new(gap_timeout: Duration) -> Self {
        Self {
            sessions: HashMap::new(),
            gap_timeout,
        }
    }

    pub fn should_create_new_session(
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

    pub fn update_session(&mut self, session_key: &str, timestamp: NaiveDateTime) {
        self.sessions.insert(session_key.to_string(), timestamp);
    }
}