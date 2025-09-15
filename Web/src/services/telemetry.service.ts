// Telemetry Service for Anonymous Usage Statistics
// This service collects anonymous usage data to help improve the application
// No personal information is collected, and users can opt-out via TELEMETRY_ENABLED env var

interface TelemetryConfig {
  enabled: boolean;
  userId: string; // Anonymous UUID
  sessionId: string;
  appVersion: string;
  environment: 'development' | 'production';
}

interface TelemetryEvent {
  event: string;
  properties?: Record<string, any>;
  timestamp: number;
}

class TelemetryService {
  private config: TelemetryConfig | null = null;
  private queue: TelemetryEvent[] = [];
  private isInitialized = false;
  private flushInterval: NodeJS.Timeout | null = null;

  // PostHog configuration (you'll need to sign up for free at posthog.com)
  // Or use your self-hosted instance
  private readonly POSTHOG_API_KEY = process.env.VITE_POSTHOG_API_KEY || '';
  private readonly POSTHOG_HOST = process.env.VITE_POSTHOG_HOST || 'https://app.posthog.com';

  async initialize(): Promise<void> {
    if (this.isInitialized) return;

    try {
      // Check if telemetry is enabled from backend
      const response = await fetch('/api/system/telemetry-status');
      const data = await response.json();

      if (!data.enabled) {
        console.log('Telemetry is disabled by user preference');
        return;
      }

      // Generate or retrieve anonymous user ID
      let userId = localStorage.getItem('telemetry_user_id');
      if (!userId) {
        userId = this.generateUUID();
        localStorage.setItem('telemetry_user_id', userId);
      }

      // Generate session ID
      const sessionId = this.generateUUID();

      this.config = {
        enabled: data.enabled,
        userId,
        sessionId,
        appVersion: data.version || '1.0.0',
        environment: import.meta.env.MODE === 'production' ? 'production' : 'development'
      };

      this.isInitialized = true;

      // Send initialization event
      this.track('app_initialized', {
        version: this.config.appVersion,
        environment: this.config.environment,
        features_enabled: {
          mock_mode: localStorage.getItem('mockMode') === 'true',
          theme: localStorage.getItem('theme_name') || 'default'
        }
      });

      // Start flush interval (send events every 30 seconds)
      this.flushInterval = setInterval(() => this.flush(), 30000);

      // Flush on page unload
      window.addEventListener('beforeunload', () => this.flush());

    } catch (error) {
      console.error('Failed to initialize telemetry:', error);
      this.isInitialized = false;
    }
  }

  track(event: string, properties?: Record<string, any>): void {
    if (!this.isInitialized || !this.config?.enabled) return;

    // Sanitize properties to remove any potential PII
    const sanitizedProperties = this.sanitizeProperties(properties);

    this.queue.push({
      event,
      properties: {
        ...sanitizedProperties,
        session_id: this.config.sessionId,
        timestamp: Date.now()
      },
      timestamp: Date.now()
    });

    // Flush if queue is getting large
    if (this.queue.length >= 10) {
      this.flush();
    }
  }

  trackPageView(page: string, properties?: Record<string, any>): void {
    this.track('page_view', {
      page,
      ...properties
    });
  }

  trackError(error: Error, context?: Record<string, any>): void {
    if (!this.isInitialized || !this.config?.enabled) return;

    this.track('error', {
      error_message: error.message,
      error_stack: error.stack,
      context: this.sanitizeProperties(context)
    });
  }

  trackFeatureUsage(feature: string, properties?: Record<string, any>): void {
    this.track(`feature_${feature}`, properties);
  }

  private sanitizeProperties(properties?: Record<string, any>): Record<string, any> {
    if (!properties) return {};

    const sanitized: Record<string, any> = {};

    for (const [key, value] of Object.entries(properties)) {
      // Skip any fields that might contain PII
      if (this.isPotentialPII(key)) continue;

      // Sanitize values
      if (typeof value === 'string') {
        // Remove IPs, emails, etc.
        sanitized[key] = this.sanitizeString(value);
      } else if (typeof value === 'object' && value !== null) {
        sanitized[key] = this.sanitizeProperties(value);
      } else {
        sanitized[key] = value;
      }
    }

    return sanitized;
  }

  private isPotentialPII(key: string): boolean {
    const piiKeywords = ['email', 'password', 'token', 'key', 'secret', 'ip', 'address', 'phone', 'ssn', 'credit'];
    return piiKeywords.some(keyword => key.toLowerCase().includes(keyword));
  }

  private sanitizeString(value: string): string {
    // Remove email addresses
    value = value.replace(/[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}/g, '[email]');

    // Remove IP addresses (basic pattern)
    value = value.replace(/\b(?:\d{1,3}\.){3}\d{1,3}\b/g, '[ip]');

    // Remove potential tokens/keys (long alphanumeric strings)
    value = value.replace(/\b[a-zA-Z0-9]{32,}\b/g, '[token]');

    return value;
  }

  private async flush(): Promise<void> {
    if (this.queue.length === 0 || !this.config?.enabled) return;

    const events = [...this.queue];
    this.queue = [];

    try {
      // If using PostHog
      if (this.POSTHOG_API_KEY) {
        await this.sendToPostHog(events);
      } else {
        // Fallback to custom endpoint
        await this.sendToCustomEndpoint(events);
      }
    } catch (error) {
      console.error('Failed to send telemetry:', error);
      // Re-add events to queue on failure
      this.queue.unshift(...events);
    }
  }

  private async sendToPostHog(events: TelemetryEvent[]): Promise<void> {
    const batch = events.map(e => ({
      event: e.event,
      properties: {
        ...e.properties,
        distinct_id: this.config!.userId,
        $lib: 'lancache-manager',
        $lib_version: this.config!.appVersion
      },
      timestamp: new Date(e.timestamp).toISOString()
    }));

    await fetch(`${this.POSTHOG_HOST}/batch/`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        api_key: this.POSTHOG_API_KEY,
        batch
      })
    });
  }

  private async sendToCustomEndpoint(events: TelemetryEvent[]): Promise<void> {
    // Send to your own analytics endpoint
    await fetch('/api/telemetry/events', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        userId: this.config!.userId,
        sessionId: this.config!.sessionId,
        events
      })
    });
  }

  private generateUUID(): string {
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
      const r = (Math.random() * 16) | 0;
      const v = c === 'x' ? r : (r & 0x3) | 0x8;
      return v.toString(16);
    });
  }

  dispose(): void {
    if (this.flushInterval) {
      clearInterval(this.flushInterval);
    }
    this.flush();
  }
}

// Export singleton instance
export const telemetryService = new TelemetryService();

// Helper hooks for React components
export function useTrackEvent() {
  return (event: string, properties?: Record<string, any>) => {
    telemetryService.track(event, properties);
  };
}

export function useTrackPageView() {
  return (page: string, properties?: Record<string, any>) => {
    telemetryService.trackPageView(page, properties);
  };
}