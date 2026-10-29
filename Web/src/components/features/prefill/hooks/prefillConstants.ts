/**
 * Constants for the prefill feature
 */

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
