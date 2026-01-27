import { API_BASE } from '../utils/constants';
import authService from './auth.service';

/**
 * Heartbeat service to keep session alive and update last seen timestamp
 * Sends periodic updates to the server when user is active and page is visible
 */
class HeartbeatService {
  private heartbeatInterval: NodeJS.Timeout | null = null;
  private isActive: boolean = true;
  private isPageVisible: boolean = true;
  private visibilityHandler: (() => void) | null = null;

  /**
   * Start sending heartbeats every 30 seconds when user is active and page is visible
   */
  startHeartbeat(): void {
    // Clear any existing interval
    this.stopHeartbeat();

    // Set up Page Visibility API listener
    this.setupVisibilityListener();

    // Send initial heartbeat if page is visible
    if (this.isPageVisible) {
      this.sendHeartbeat();
    }

    // Set up interval to send heartbeat every 30 seconds
    this.heartbeatInterval = setInterval(() => {
      // Only send heartbeat if user is active AND page is visible
      if (this.isActive && this.isPageVisible) {
        this.sendHeartbeat();
      }
    }, 30000); // 30 seconds
  }

  /**
   * Set up Page Visibility API listener to pause heartbeats when tab is hidden
   */
  private setupVisibilityListener(): void {
    // Remove any existing listener
    this.removeVisibilityListener();

    // Update initial visibility state
    this.isPageVisible = !document.hidden;

    // Create handler for visibility changes
    this.visibilityHandler = () => {
      const wasVisible = this.isPageVisible;
      this.isPageVisible = !document.hidden;

      if (this.isPageVisible && !wasVisible) {
        // Tab became visible - send immediate heartbeat
        this.sendHeartbeat();
      }
    };

    document.addEventListener('visibilitychange', this.visibilityHandler);
  }

  /**
   * Remove visibility listener
   */
  private removeVisibilityListener(): void {
    if (this.visibilityHandler) {
      document.removeEventListener('visibilitychange', this.visibilityHandler);
      this.visibilityHandler = null;
    }
  }

  /**
   * Stop sending heartbeats
   */
  stopHeartbeat(): void {
    if (this.heartbeatInterval) {
      clearInterval(this.heartbeatInterval);
      this.heartbeatInterval = null;
    }
    this.removeVisibilityListener();
  }

  /**
   * Update activity status
   * @param active Whether the user is currently active
   */
  setActive(active: boolean): void {
    this.isActive = active;

    if (active && this.isPageVisible) {
      // User became active and page is visible - send immediate heartbeat
      this.sendHeartbeat();
    }
  }

  /**
   * Send a heartbeat to update last seen timestamp
   */
  private async sendHeartbeat(): Promise<void> {
    try {
      // Heartbeats are meaningful for both authenticated and guest sessions
      // Only skip when completely unauthenticated (no session at all)
      if (!authService.isRegistered() && !authService.isGuestModeActive()) {
        return;
      }

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

      if (!response.ok && response.status !== 404 && response.status !== 401 && response.status !== 403) {
        // Only log non-404/non-auth errors (404 is expected after app restart; 401/403 can happen during auth transitions)
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
