import { PERSISTENT_PREFILL_SERVICES } from '@components/features/prefill/persistentPrefillConstants';
import { getEventName } from '@components/features/prefill/hooks/prefillConstants';
import type { PersistentPrefillServiceId } from '@components/features/prefill/persistentPrefillTypes';

const PERSISTENT_PREFILL_EVENT_KEY: Record<PersistentPrefillServiceId, string> = {
  Steam: 'steam',
  Epic: 'epic',
  Xbox: 'xbox',
  BattleNet: 'battlenet',
  Riot: 'riot'
};

const CONTAINER_WATCH_BASE_EVENTS = [
  'AuthStateChanged',
  'DaemonSessionUpdated',
  'DaemonSessionCreated',
  'DaemonSessionTerminated',
  'PrefillStateChanged'
] as const;

const persistentPrefillServiceToEventKey = (serviceId: PersistentPrefillServiceId): string =>
  PERSISTENT_PREFILL_EVENT_KEY[serviceId];

/** Resolves the platform-specific CredentialChallenge event name. */
export const getPersistentPrefillCredentialChallengeEvent = (
  serviceId: PersistentPrefillServiceId
): string => getEventName('CredentialChallenge', persistentPrefillServiceToEventKey(serviceId));

/** All SignalR events that should trigger a persistent-container list refresh. */
export const PERSISTENT_PREFILL_CONTAINER_SIGNALR_EVENTS = PERSISTENT_PREFILL_SERVICES.flatMap(
  ({ service }) =>
    CONTAINER_WATCH_BASE_EVENTS.map((base) =>
      getEventName(base, persistentPrefillServiceToEventKey(service))
    )
);
