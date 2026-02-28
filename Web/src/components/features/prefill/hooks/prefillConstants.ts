/**
 * Constants for the prefill feature
 */

/**
 * Maximum age of prefill progress data in sessionStorage before it's considered stale (2 hours)
 */
export const PREFILL_SESSION_TIMEOUT_MS = 2 * 60 * 60 * 1000;

/**
 * Duration of the animation for cached games (in milliseconds)
 */
export const ANIMATION_DURATION_MS = 2000;

/**
 * Delay after animation completes before processing next item (in milliseconds)
 */
export const ANIMATION_COMPLETION_DELAY_MS = 100;

/**
 * Time window for showing completion notifications after reconnecting (5 minutes)
 */
export const COMPLETION_NOTIFICATION_WINDOW_MS = 5 * 60 * 1000;

/** Maps generic event names to service-specific event names for Epic */
const EPIC_EVENT_MAP: Record<string, string> = {
  AuthStateChanged: 'EpicAuthStateChanged',
  SessionSubscribed: 'SessionSubscribed',
  SessionEnded: 'EpicSessionEnded',
  DaemonSessionTerminated: 'EpicDaemonSessionTerminated',
  PrefillProgress: 'EpicPrefillProgress',
  StatusChanged: 'EpicStatusChanged',
  PrefillStateChanged: 'EpicPrefillStateChanged',
  DaemonSessionCreated: 'EpicDaemonSessionCreated',
  DaemonSessionUpdated: 'EpicDaemonSessionUpdated',
  PrefillHistoryUpdated: 'EpicPrefillHistoryUpdated',
  CredentialChallenge: 'EpicCredentialChallenge'
};

/** Resolves a generic SignalR event name to a service-specific name */
export function getEventName(base: string, serviceId: string): string {
  if (serviceId === 'epic') {
    return EPIC_EVENT_MAP[base] ?? base;
  }
  return base;
}
