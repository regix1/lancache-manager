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

/**
 * Watchdog window after clicking Cancel. If no terminal PrefillStateChanged/PrefillProgress
 * event arrives within this window, the bar is force-cleared so the UI can't get stuck
 * "Cancelling..." forever (diagnostic I8). Lowered to 5s (O6) so a stale "Cancelling..." row
 * doesn't linger when the daemon is slow to emit the cancelled terminal.
 */
export const CANCEL_WATCHDOG_MS = 5 * 1000;

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

/** Maps generic event names to service-specific event names for Battle.net */
const BATTLENET_EVENT_MAP: Record<string, string> = {
  AuthStateChanged: 'BattleNetAuthStateChanged',
  SessionSubscribed: 'SessionSubscribed',
  SessionEnded: 'BattleNetSessionEnded',
  DaemonSessionTerminated: 'BattleNetDaemonSessionTerminated',
  PrefillProgress: 'BattleNetPrefillProgress',
  StatusChanged: 'BattleNetStatusChanged',
  PrefillStateChanged: 'BattleNetPrefillStateChanged',
  DaemonSessionCreated: 'BattleNetDaemonSessionCreated',
  DaemonSessionUpdated: 'BattleNetDaemonSessionUpdated',
  PrefillHistoryUpdated: 'BattleNetPrefillHistoryUpdated',
  CredentialChallenge: 'BattleNetCredentialChallenge'
};

/** Maps generic event names to service-specific event names for Riot */
const RIOT_EVENT_MAP: Record<string, string> = {
  AuthStateChanged: 'RiotAuthStateChanged',
  SessionSubscribed: 'SessionSubscribed',
  SessionEnded: 'RiotSessionEnded',
  DaemonSessionTerminated: 'RiotDaemonSessionTerminated',
  PrefillProgress: 'RiotPrefillProgress',
  StatusChanged: 'RiotStatusChanged',
  PrefillStateChanged: 'RiotPrefillStateChanged',
  DaemonSessionCreated: 'RiotDaemonSessionCreated',
  DaemonSessionUpdated: 'RiotDaemonSessionUpdated',
  PrefillHistoryUpdated: 'RiotPrefillHistoryUpdated',
  CredentialChallenge: 'RiotCredentialChallenge'
};

/** Maps generic event names to service-specific event names for Xbox */
const XBOX_EVENT_MAP: Record<string, string> = {
  AuthStateChanged: 'XboxAuthStateChanged',
  SessionSubscribed: 'SessionSubscribed',
  SessionEnded: 'XboxSessionEnded',
  DaemonSessionTerminated: 'XboxDaemonSessionTerminated',
  PrefillProgress: 'XboxPrefillProgress',
  StatusChanged: 'XboxStatusChanged',
  PrefillStateChanged: 'XboxPrefillStateChanged',
  DaemonSessionCreated: 'XboxDaemonSessionCreated',
  DaemonSessionUpdated: 'XboxDaemonSessionUpdated',
  PrefillHistoryUpdated: 'XboxPrefillHistoryUpdated',
  CredentialChallenge: 'XboxCredentialChallenge'
};

/** Resolves a generic SignalR event name to a service-specific name */
export function getEventName(base: string, serviceId: string): string {
  if (serviceId === 'epic') {
    return EPIC_EVENT_MAP[base] ?? base;
  }
  if (serviceId === 'battlenet') {
    return BATTLENET_EVENT_MAP[base] ?? base;
  }
  if (serviceId === 'riot') {
    return RIOT_EVENT_MAP[base] ?? base;
  }
  if (serviceId === 'xbox') {
    return XBOX_EVENT_MAP[base] ?? base;
  }
  return base;
}
