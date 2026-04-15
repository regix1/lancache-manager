import type { IRetryPolicy, RetryContext } from '@microsoft/signalr';

/**
 * Infinite-backoff retry policy for the SignalR hub connection.
 *
 * Behavior:
 *  - Returns `null` (pauses reconnection) while the page is hidden. The outer
 *    visibility handler is responsible for kicking the connection back alive
 *    when the tab becomes visible again.
 *  - Otherwise follows a ladder of delays:
 *      attempt 0 -> 0ms (immediate)
 *      attempt 1 -> 2s
 *      attempt 2 -> 5s
 *      attempt 3 -> 10s
 *      attempt 4 -> 30s
 *      attempt 5+ -> 60s +/- 25% jitter (min 5s)
 *  - Never returns `null` to give up — reconnection attempts continue forever
 *    while the page is visible.
 */
export class InfiniteBackoffRetryPolicy implements IRetryPolicy {
  constructor(private readonly isPageVisible: () => boolean) {}

  nextRetryDelayInMilliseconds(retryContext: RetryContext): number | null {
    // Pause reconnection while the page is hidden; the visibility handler
    // will re-establish the connection when the tab becomes active again.
    if (!this.isPageVisible()) {
      return null;
    }

    const attempt = retryContext.previousRetryCount;

    if (attempt === 0) return 0;
    if (attempt === 1) return 2000;
    if (attempt === 2) return 5000;
    if (attempt === 3) return 10000;
    if (attempt === 4) return 30000;

    // Attempt 5+ — 60s base with +/- 25% jitter, never below 5s.
    const base = 60000;
    const jitter = Math.round((Math.random() * 2 - 1) * 0.25 * base);
    return Math.max(5000, base + jitter);
  }
}
