import { API_BASE } from '../utils/constants';
import authService from './auth.service';

/**
 * Heartbeat service to keep session alive and update last seen timestamp
 * Sends periodic updates to the server when user is active
 */
class HeartbeatService {
  private heartbeatInterval: NodeJS.Timeout | null = null;
  private isActive: boolean = true;

  /**
   * Start sending heartbeats every 30 seconds when user is active
   */
  startHeartbeat(): void {
    // Clear any existing interval
    this.stopHeartbeat();

    // Send initial heartbeat
    this.sendHeartbeat();

    // Set up interval to send heartbeat every 30 seconds
    this.heartbeatInterval = setInterval(() => {
      if (this.isActive) {
        this.sendHeartbeat();
      }
    }, 30000); // 30 seconds

    console.log('[Heartbeat] Started sending heartbeats every 30 seconds');
  }

  /**
   * Stop sending heartbeats
   */
  stopHeartbeat(): void {
    if (this.heartbeatInterval) {
      clearInterval(this.heartbeatInterval);
      this.heartbeatInterval = null;
      console.log('[Heartbeat] Stopped sending heartbeats');
    }
  }

  /**
   * Update activity status
   * @param active Whether the user is currently active
   */
  setActive(active: boolean): void {
    this.isActive = active;

    if (active) {
      // User became active - send immediate heartbeat
      this.sendHeartbeat();
    }
  }

  /**
   * Send a heartbeat to update last seen timestamp
   */
  private async sendHeartbeat(): Promise<void> {
    try {
      const sessionId = authService.getDeviceId();

      if (!sessionId) {
        console.warn('[Heartbeat] No session ID available, skipping heartbeat');
        return;
      }

      const response = await fetch(`${API_BASE}/sessions/${sessionId}/last-seen`, {
        method: 'PATCH',
        credentials: 'include',
        headers: authService.getAuthHeaders()
      });

      if (!response.ok) {
        // Don't log 404s - session might not exist yet or was revoked
        if (response.status !== 404) {
          console.warn(`[Heartbeat] Failed to send heartbeat: ${response.status}`);
        }
      }
    } catch (error) {
      // Silently fail - heartbeats are non-critical
      // Connection errors are expected during network issues
    }
  }
}

export default new HeartbeatService();
