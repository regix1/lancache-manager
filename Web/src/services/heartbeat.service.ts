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
      const deviceId = authService.getDeviceId();

      if (!deviceId) {
        console.warn('[Heartbeat] No device ID available, skipping heartbeat');
        return;
      }

      // Use the new "current" endpoint that reads device ID from headers
      // This prevents 404 errors when sessions are cleared on app restart
      const response = await fetch(`${API_BASE}/sessions/current/last-seen`, {
        method: 'PATCH',
        credentials: 'include',
        headers: authService.getAuthHeaders()
      });

      if (!response.ok && response.status !== 404) {
        // Only log non-404 errors (404 is expected after app restart)
        // Heartbeats are non-critical - session will be auto-restored on next auth check
        console.warn(`[Heartbeat] Failed to send heartbeat: ${response.status}`);
      }
    } catch (error) {
      // Silently fail - heartbeats are non-critical
      // Connection errors are expected during network issues
    }
  }
}

export default new HeartbeatService();
