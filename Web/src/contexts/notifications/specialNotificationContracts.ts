/**
 * Declarative registry of special-case SignalR event subscriptions that don't
 * fit the standard Started->Progress->Complete pattern handled by
 * {@link useNotificationHandlers}.
 *
 * This file collapses what used to be 16 imperative `signalR.on(...)` +
 * `signalR.off(...)` lines in NotificationsContext.tsx into a single
 * iterator-driven `useEffect`.
 *
 * The actual handler bodies still live in {@link createSpecialCaseHandlers} —
 * this registry only describes the `{event, handler}` binding for each
 * special-case lifecycle. That keeps the refactor a minimum-delta move
 * (collapse wiring, don't rewrite handlers).
 *
 * Invariants (load-bearing):
 * - `details.operationId` is carried through on every depot_mapping /
 *   database_reset / epic_game_mapping notification. Cancel plumbing in
 *   `UniversalNotificationBar` depends on it.
 * - `createStatusAwareProgressHandler` MERGES details across progress ticks
 *   (`prev.details` spread into new details). Do not replace the handler with
 *   anything that uses set-semantics instead of merge-semantics.
 * - `EpicGameMappingsUpdated` is a one-shot completion with its own payload
 *   shape (`totalGames`, `newGames`, `updatedGames`) — NOT folded into the
 *   standard completion flow.
 * - `SteamSessionError` is an error toast (not a lifecycle) — it shares this
 *   registry for wiring uniformity but produces a notification with
 *   `details.notificationType: 'error'` instead of operation details.
 */
import type { EventHandler } from '../SignalRContext/types';
import type { SpecialCaseHandlers } from './specialCaseHandlers';

interface SpecialNotificationSubscription {
  event: string;
  handler: EventHandler;
}

interface SpecialNotificationContract {
  /** Human-readable label for debugging/logging. Groups related events. */
  key: string;
  /**
   * Given the `handlers` object produced by {@link createSpecialCaseHandlers},
   * return the `{event, handler}` pairs to register with SignalR.
   *
   * Each contract entry may subscribe to multiple events that share the same
   * underlying notification lifecycle (e.g. depot_mapping binds Started +
   * Progress + Complete to three separate handler functions).
   */
  subscribe: (handlers: SpecialCaseHandlers) => SpecialNotificationSubscription[];
}

export const SPECIAL_NOTIFICATION_CONTRACTS: SpecialNotificationContract[] = [
  {
    key: 'depot_mapping',
    subscribe: (h) => [
      { event: 'DepotMappingStarted', handler: h.handleDepotMappingStarted as EventHandler },
      { event: 'DepotMappingProgress', handler: h.handleDepotMappingProgress as EventHandler },
      { event: 'DepotMappingComplete', handler: h.handleDepotMappingComplete as EventHandler }
    ]
  },
  {
    key: 'database_reset',
    subscribe: (h) => [
      { event: 'DatabaseResetStarted', handler: h.handleDatabaseResetStarted as EventHandler },
      { event: 'DatabaseResetProgress', handler: h.handleDatabaseResetProgress as EventHandler }
    ]
  },
  {
    key: 'epic_game_mapping',
    subscribe: (h) => [
      { event: 'EpicMappingProgress', handler: h.handleEpicMappingProgress as EventHandler },
      {
        event: 'EpicGameMappingsUpdated',
        handler: h.handleEpicGameMappingsUpdated as EventHandler
      }
    ]
  },
  {
    key: 'steam_session_error',
    subscribe: (h) => [
      { event: 'SteamSessionError', handler: h.handleSteamSessionError as EventHandler }
    ]
  }
];
