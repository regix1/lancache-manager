/**
 * The started/progress/completion handlers behind every notification card, plus the two rules
 * both card sources have to agree on.
 *
 * This file is NOT specific to one kind of operation. `useNotificationHandlers` builds these for
 * all 28 registry types, so a single game removal and a scheduled scan come through here alike.
 *
 * Where to look for the rest of a card's life:
 *   - a batch's OWN card is created in `BulkRemovalContext`, not here, and its run loop is
 *     `hooks/useBatchQueue.ts`
 *   - cards rebuilt after a reconnect or a tab switch come from `recovery.ts`
 *   - what a card looks like is decided in `UniversalNotificationBar`, via `utils/statusVariant`
 *
 * Batches and singles MEET here, deliberately. A batch card declares which per-item notification
 * types its own items produce (`details.itemTypes`), and `findBulkCardOwningType` below is what
 * stops those items opening a second card next to the batch card that already reports them.
 * Keeping that check in one place is the point: it once lived in two files and a fix applied to
 * one of them left the other still broken.
 */

import type {
  NotificationType,
  NotificationProgressMode,
  NotificationStatus,
  UnifiedNotification,
  SetNotifications,
  ScheduleAutoDismiss,
  CancelAutoDismissTimer,
  NotificationEvents,
  NotificationEvent,
  NotificationRegistryEntry
} from './types';
import { isTerminalNotificationStatus } from './notificationStatus';
import type { OperationWaitingCompleteEvent } from '../SignalRContext/types';
import { storage } from '@utils/storage';
import i18n from '@/i18n';
import {
  CANCELLED_NOTIFICATION_DELAY_MS,
  FULL_PROGRESS_PERCENT,
  GENERIC_CANCELLED_I18N_KEY,
  GENERIC_COMPLETION_I18N_KEY,
  GENERIC_FAILURE_I18N_KEY,
  GENERIC_SKIPPED_I18N_KEY,
  LIVE_ONLY_CANCEL_DETAIL_KEYS,
  OPERATION_WAITING_I18N_KEYS
} from './constants';

interface NotificationEventOptions {
  events?: NotificationEvents;
  eventName?: string;
  replay?: boolean;
}

/** Record transport values before any card update. A claimed terminal never changes. */
export function rememberEvent(
  events: NotificationEvents,
  type: NotificationType,
  phase: NotificationEvent['phase'] | 'waiting' | 'handoff',
  eventName: string,
  value: unknown
): boolean {
  if (!value || typeof value !== 'object') return false;
  const body = value as Record<string, unknown>;
  const operationId = eventOperationId(value);
  if (!operationId) return true;

  if (phase === 'handoff') {
    const nextOperationId = body.nextOperationId;
    if (
      body.promoted === true &&
      (typeof nextOperationId !== 'string' ||
        !nextOperationId.trim() ||
        nextOperationId === operationId)
    )
      return false;
    if (events.handoffs.has(operationId) || events.terminals.has(operationId)) return false;
    const handoff = value as OperationWaitingCompleteEvent;
    events.handoffs.set(operationId, { ...handoff });
    events.waiting.delete(operationId);
    events.terminals.set(operationId, {
      operationId,
      status: handoff.promoted
        ? 'completed'
        : handoff.cancelled
          ? 'cancelled'
          : handoff.skipped
            ? 'skipped'
            : 'failed',
      error: handoff.error,
      eventName
    });
    events.acknowledgedIds.add(`terminal_${operationId}`);
  } else {
    if (events.terminals.has(operationId)) return false;
    if (
      type === 'game_detection' &&
      typeof body.parentOperationId === 'string' &&
      body.parentOperationId.trim()
    ) {
      events.children.set(operationId, body.parentOperationId);
    }
    if (phase === 'waiting') {
      events.waiting.add(operationId);
    } else {
      const terminalStatus =
        typeof body.status === 'string' &&
        ['completed', 'failed', 'cancelled', 'skipped'].includes(body.status)
          ? body.status
          : undefined;
      if (phase === 'complete' || terminalStatus) {
        const error = typeof body.error === 'string' && body.error.trim() ? body.error : undefined;
        const status =
          body.cancelled === true || terminalStatus === 'cancelled'
            ? 'cancelled'
            : body.skipped === true || terminalStatus === 'skipped'
              ? 'skipped'
              : terminalStatus === 'failed' || body.success === false || error
                ? 'failed'
                : 'completed';
        events.terminals.set(operationId, { operationId, status, error, eventName });
        events.acknowledgedIds.add(`terminal_${operationId}`);
        events.waiting.delete(operationId);
      }
    }
  }

  const revision = ++events.revision;
  events.revisions.set(operationId, revision);
  events.typeRevisions.set(type, revision);
  if (phase !== 'waiting' && phase !== 'handoff') {
    const retained = events.records.get(operationId) ?? {};
    events.records.delete(operationId);
    events.records.set(operationId, {
      ...retained,
      [phase]: { type, phase, eventName, body: { ...body }, revision }
    });
  }
  if (events.records.size > 128) {
    for (const id of events.records.keys()) {
      if (events.records.size <= 128) break;
      if (events.waiting.has(id) || events.held.has(id)) continue;
      if (
        [...events.handoffs.values()].some(
          (link) => link.nextOperationId === id && !events.terminals.has(id)
        )
      )
        continue;
      events.records.delete(id);
    }
  }
  return true;
}

function excludeChild(
  config: NotificationEventOptions & {
    type: NotificationType;
    storageKey: string;
    storesCardsById?: boolean;
  },
  event: unknown,
  setNotifications: SetNotifications
): boolean {
  const operationId = eventOperationId(event);
  const parentOperationId = (event as { parentOperationId?: unknown } | null)?.parentOperationId;
  if (
    config.type !== 'game_detection' ||
    !operationId ||
    (!(typeof parentOperationId === 'string' && parentOperationId.trim()) &&
      !config.events?.children.has(operationId))
  )
    return false;
  setNotifications((prev) => {
    for (const saved of readPersistedCards(config.storageKey)) {
      if (saved.details?.operationId === operationId)
        clearPersistedNotificationIfTargeted(
          config.storageKey,
          event,
          saved.id,
          config.storesCardsById
        );
    }
    return prev.filter(
      (card) => card.type !== 'game_detection' || card.details?.operationId !== operationId
    );
  });
  return true;
}

/**
 * Statuses a live event promotes back to 'running'.
 *
 * A progress event is proof the operation is alive, so a card parked in a pre-run status must not
 * ignore its OWN operation's events: a queued card whose promotion (Started) event was missed
 * would otherwise swallow every later progress and completion event and sit frozen forever.
 * 'cancelling' is deliberately NOT promotable: those cards still take progress updates, but must
 * keep showing that a cancel is in flight.
 */
const PROMOTABLE_TO_RUNNING: readonly NotificationStatus[] = ['waiting', 'pending'];

const promoteStatus = (status: NotificationStatus): NotificationStatus =>
  PROMOTABLE_TO_RUNNING.includes(status) ? 'running' : status;

/** operationId carried by any lifecycle event on the wire. */
function eventOperationId(event: unknown): string | undefined {
  const operationId = (event as { operationId?: unknown } | null | undefined)?.operationId;
  return typeof operationId === 'string' ? operationId : undefined;
}

/**
 * Text for an operation parked in the wait queue. Two things describe that state and carry the same
 * two fields: the live `OperationWaiting` push and a row from the `/api/operations/waiting`
 * reconciliation. They must word it identically, or the sentence changes when a reconnect swaps one
 * source for the other.
 */
export function waitingCardMessage(source: {
  name?: string;
  blockedByName?: string | null;
}): string {
  if (source.name) {
    return source.blockedByName
      ? i18n.t(OPERATION_WAITING_I18N_KEYS.NAMED_BLOCKED, {
          name: source.name,
          blocker: source.blockedByName
        })
      : i18n.t(OPERATION_WAITING_I18N_KEYS.NAMED, { name: source.name });
  }
  return source.blockedByName
    ? i18n.t(OPERATION_WAITING_I18N_KEYS.BLOCKED, { blocker: source.blockedByName })
    : i18n.t(OPERATION_WAITING_I18N_KEYS.DEFAULT);
}

/** Skip opening a new per-item singleton while the bulk card whose items produce it owns progress. */
function findBulkCardOwningType(
  type: NotificationType,
  notifications: UnifiedNotification[]
): UnifiedNotification | undefined {
  return notifications.find(
    (notification) =>
      notification.type === 'bulk_removal' &&
      // 'waiting' as well as 'running': the card turns purple while its current item is parked
      // behind another operation, and it still owns the per-item cards for the whole run. Matching
      // only 'running' would let a second card appear for the item it is already reporting on.
      (notification.status === 'running' || notification.status === 'waiting') &&
      notification.details?.itemTypes?.includes(type) === true
  );
}

function suppressNewItemCardDuringBulk(
  type: NotificationType,
  notifications: UnifiedNotification[]
): boolean {
  return findBulkCardOwningType(type, notifications) !== undefined;
}

/**
 * The batch card whose own in-flight item IS this queued operation.
 *
 * Declaring an item type is not the same as having started the work: a batch shares its types
 * with a second batch and with every removal a user starts from a page, so the type alone cannot
 * decide whose queued operation this is. A batch publishes its current item's operation id while
 * that item is in flight, and a card carrying a DIFFERENT id is reporting other work - folding
 * the queued operation into it would relabel a card that has nothing to do with the event and
 * leave the queued operation with no card of its own to cancel from.
 *
 * A batch that has not published an id yet is claimed only while its item's request is still on
 * the wire. The queue announces a parked operation from inside that request, so the push really
 * can beat the response home, and that one round trip is the whole reason the type-level answer
 * survives at all. Once the request is answered an empty id means the batch has nothing to claim -
 * it is between items, or its item was deduplicated onto a removal whose id it refused - and a
 * queued operation of the same type belongs to somebody else for as long as that lasts.
 */
export function findBulkCardOwningOperation(
  type: NotificationType,
  operationId: string | undefined,
  notifications: UnifiedNotification[]
): UnifiedNotification | undefined {
  const owningBulk = findBulkCardOwningType(type, notifications);
  if (!owningBulk) return undefined;
  const currentOperationId = owningBulk.details?.currentOperationId;
  if (currentOperationId) {
    return operationId && currentOperationId !== operationId ? undefined : owningBulk;
  }
  return owningBulk.details?.itemRequestPending === true ? owningBulk : undefined;
}

/**
 * Whether a lifecycle event is allowed to touch the card currently in its type's singleton slot.
 *
 * CRITICAL: a non-running card in that slot is NOT necessarily the same operation. Two operations
 * of one type can be live at once - the backend queues the second, and the wait-queue parks the
 * QUEUED op's 'waiting' card in the shared slot while the FIRST op is still running and still
 * emitting progress. Letting those events through would promote the queued card to running,
 * overwrite its operationId (so the X button cancels the WRONG operation), and let the running
 * op's completion auto-dismiss a card whose operation never even started.
 *
 * A known operation-id mismatch is always rejected, including for a running card. When either side
 * does not yet have an operation id, a running card keeps the historical type-level fallback; any
 * other card is touched ONLY when the event provably belongs to it (matching operationId).
 */
export function eventTargetsCard(existing: UnifiedNotification, event: unknown): boolean {
  const cardOperationId = existing.details?.operationId;
  const incomingOperationId = eventOperationId(event);
  if (cardOperationId && incomingOperationId && cardOperationId !== incomingOperationId) {
    return false;
  }
  if (existing.status === 'running') return true;
  return Boolean(cardOperationId && incomingOperationId && cardOperationId === incomingOperationId);
}

/**
 * The cards persisted under one storage key. A type with a singleton card stores that card on
 * its own; a type that owns one card per entity stores a record of card id to card, so a page
 * reload restores every one of them.
 */
export function readPersistedCards(storageKey: string): UnifiedNotification[] {
  const persisted = storage.getItem(storageKey);
  if (!persisted) {
    return [];
  }

  try {
    const parsed = JSON.parse(persisted) as
      | UnifiedNotification
      | Record<string, UnifiedNotification>;
    // A card carries its own id; a record of cards does not.
    if (typeof (parsed as UnifiedNotification).id === 'string') {
      return [parsed as UnifiedNotification];
    }
    return Object.values(parsed as Record<string, UnifiedNotification>);
  } catch {
    return [];
  }
}

/**
 * The same cards keyed by id. A card written by a build that stored this key as a single card
 * is folded into the record here, so an upgrade mid-run does not strand it.
 */
function readPersistedCardsById(storageKey: string): Record<string, UnifiedNotification> {
  const cards: Record<string, UnifiedNotification> = {};
  for (const card of readPersistedCards(storageKey)) {
    cards[card.id] = card;
  }
  return cards;
}

export function persistNotification(
  storageKey: string,
  notification: UnifiedNotification,
  storesCardsById?: boolean
): void {
  if (
    !storageKey ||
    notification.status !== 'running' ||
    notification.controlOnly ||
    notification.details?.parentOperationId
  )
    return;
  if (!storesCardsById) {
    storage.setItem(storageKey, JSON.stringify(notification));
    return;
  }

  const cards = readPersistedCardsById(storageKey);
  cards[notification.id] = notification;
  storage.setItem(storageKey, JSON.stringify(cards));
}

export function clearPersistedNotificationIfTargeted(
  storageKey: string,
  event: unknown,
  notificationId: string,
  storesCardsById?: boolean
): boolean {
  if (storesCardsById) {
    const cards = readPersistedCardsById(storageKey);
    const card = cards[notificationId];
    if (!card) {
      return true;
    }
    if (!eventTargetsCard(card, event)) {
      return false;
    }
    delete cards[notificationId];
    // The key goes away only once its last card does, so one service's terminal event cannot
    // take the other services' persisted cards with it.
    if (Object.keys(cards).length === 0) {
      storage.removeItem(storageKey);
    } else {
      storage.setItem(storageKey, JSON.stringify(cards));
    }
    return true;
  }

  const persisted = storage.getItem(storageKey);
  if (!persisted) {
    return true;
  }

  try {
    const notification = JSON.parse(persisted) as UnifiedNotification;
    if (!eventTargetsCard(notification, event)) {
      return false;
    }
  } catch {
    return false;
  }

  storage.removeItem(storageKey);
  return true;
}

/**
 * Merges incoming event details over existing card details. Stale per-operation cancel
 * flags (cancelRequested/cancelSent/cancelling) are dropped before merging whenever they
 * would otherwise survive onto a NEW operationId - otherwise a leftover flag from a
 * PREVIOUS or not-yet-known op makes the deferred-cancel watchdog in
 * UniversalNotificationBar auto-cancel a brand-new operation (the phantom-cancel half of
 * the cancel->respawn loop).
 *
 * `forNewOperationEvent` distinguishes the two call sites:
 * - createStartedHandler (forNewOperationEvent: true): a Started event only reaches this
 *   merge when the singleton card is ALREADY 'running' (see the caller), which - given the
 *   backend's one-op-per-type lock - can only mean a re-spawned/queue-promoted operation,
 *   never a second delivery for the same op. Stale flags are stripped whenever the
 *   incoming payload carries any operationId, regardless of what (if anything) the
 *   existing card's operationId was.
 * - createStatusAwareProgressHandler (default): progress events are continuations of the
 *   SAME running operation, so flags are stripped only when the existing card already had
 *   a different operationId. This preserves the legitimate deferred-cancel case (user
 *   clicks X before the card has an operationId) - the cancelRequested flag must survive
 *   until a progress event delivers that operation's first operationId.
 */
function mergeEventDetails(
  existing: UnifiedNotification['details'],
  incoming: UnifiedNotification['details'],
  forNewOperationEvent = false
): UnifiedNotification['details'] {
  if (!incoming) return existing;
  const base: NonNullable<UnifiedNotification['details']> = { ...existing };
  const incomingHasOperationId = typeof incoming.operationId === 'string';
  const shouldStripStaleCancelFlags = forNewOperationEvent
    ? incomingHasOperationId
    : incomingHasOperationId &&
      typeof base.operationId === 'string' &&
      incoming.operationId !== base.operationId;
  if (shouldStripStaleCancelFlags) {
    for (const key of LIVE_ONLY_CANCEL_DETAIL_KEYS) {
      delete base[key];
    }
  }
  return { ...base, ...incoming };
}

// ============================================================================
// Started Handler Factory
// ============================================================================

/**
 * Configuration for creating a started event handler.
 * @template T - The type of the SignalR event
 */
interface StartedHandlerConfig<T> extends NotificationEventOptions {
  canControl?: (event: T) => boolean;
  /** Optional gate that suppresses and removes the notification for this event */
  shouldDisplay?: (event: T) => boolean;
  /** The notification type this handler creates */
  type: NotificationType;
  /** Function to extract the notification ID from the event */
  getId: (event: T) => string;
  /** localStorage key for persisting the notification */
  storageKey: string;
  /** Set for a type that owns one card per entity: the key then holds a record of card id to card. */
  storesCardsById?: boolean;
  /** Default message if getMessage is not provided or returns undefined */
  defaultMessage: string;
  /** Optional function to get a custom message from the event */
  getMessage?: (event: T) => string;
  /** Optional function to get notification details from the event */
  getDetails?: (event: T) => UnifiedNotification['details'];
  /** If true, always replace existing notification (for restartable operations) */
  replaceExisting?: boolean;
  /**
   * Progress semantics for the card this handler creates. Left unset the card is determinate at
   * the 0% below, which is a lie for an operation that starts by waiting on a person.
   */
  progressMode?: NotificationProgressMode;
}

/**
 * Creates a handler function for "started" events.
 * Started handlers create new running notifications when an operation begins.
 *
 * @template T - The type of the SignalR event
 * @param config - Configuration for the handler
 * @param setNotifications - React setState function for notifications
 * @param cancelAutoDismissTimer - Optional function to cancel pending auto-dismiss
 * @returns A handler function that processes the started event
 *
 * @example
 * ```ts
 * const handleGameDetectionStarted = createStartedHandler<GameDetectionStartedEvent>(
 *   {
 *     type: 'game_detection',
 *     getId: () => NOTIFICATION_IDS.GAME_DETECTION,
 *     storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
 *     defaultMessage: 'Detecting games...',
 *     getDetails: (e) => ({ operationId: e.operationId })
 *   },
 *   setNotifications,
 *   cancelAutoDismissTimer
 * );
 * ```
 */
export function operationCardId(operationId: string): string {
  return `operation_${operationId}`;
}

/** @public */
export function createStartedHandler<T>(
  config: StartedHandlerConfig<T>,
  setNotifications: SetNotifications,
  cancelAutoDismissTimer?: CancelAutoDismissTimer
): (event: T) => void {
  return (event: T): void => {
    const accepted =
      config.replay ||
      !config.events ||
      rememberEvent(config.events, config.type, 'started', config.eventName ?? '', event);
    if (excludeChild(config, event, setNotifications) || !accepted) return;
    const operationId = eventOperationId(event);
    if (operationId && config.events?.terminals.has(operationId)) return;
    const notificationId = config.getId(event);

    setNotifications((prev) => {
      const exact = operationId
        ? prev.find((n) => n.type === config.type && n.details?.operationId === operationId)
        : undefined;
      const slot = prev.find((n) => n.id === notificationId);
      const existing = exact ?? (slot && eventTargetsCard(slot, event) ? slot : undefined);
      if (existing && isTerminalNotificationStatus(existing.status)) return prev;
      const hidden = config.shouldDisplay?.(event) === false;
      if (!existing && slot && !hidden && !isTerminalNotificationStatus(slot.status)) return prev;
      if (hidden && !config.canControl?.(event)) {
        if (!existing) return prev;
        clearPersistedNotificationIfTargeted(
          config.storageKey,
          event,
          existing.id,
          config.storesCardsById
        );
        cancelAutoDismissTimer?.(existing.id);
        return prev.filter((n) => n !== existing);
      }
      if (hidden && !operationId) return prev;
      if (operationId && findBulkCardOwningOperation(config.type, operationId, prev)) return prev;
      if (!existing && suppressNewItemCardDuringBulk(config.type, prev)) return prev;
      const id =
        existing?.id ?? (hidden && operationId ? operationCardId(operationId) : notificationId);
      cancelAutoDismissTimer?.(id);
      const card: UnifiedNotification = {
        ...existing,
        id,
        type: config.type,
        status: existing?.details?.cancelRequested ? 'cancelling' : 'running',
        controlOnly: hidden || undefined,
        message: existing?.message || config.getMessage?.(event) || config.defaultMessage,
        startedAt: existing?.startedAt ?? new Date(),
        instanceVersion: existing?.instanceVersion ?? (slot?.instanceVersion ?? 0) + 1,
        progress: existing?.progress ?? (hidden ? undefined : 0),
        progressMode: existing?.progressMode ?? config.progressMode,
        details: mergeEventDetails(existing?.details, {
          ...config.getDetails?.(event),
          ...(operationId ? { operationId } : {})
        })
      };
      persistNotification(config.storageKey, card, config.storesCardsById);
      return existing
        ? prev.map((n) => (n === existing ? card : n))
        : [...prev.filter((n) => n.id !== id), card];
    });
  };
}

// ============================================================================
// Completion Handler Factory
// ============================================================================

/**
 * Configuration for creating a completion event handler.
 * @template T - The type of the SignalR event (must have success and optional message)
 */
interface CompletionHandlerConfig<T> extends NotificationEventOptions {
  /** Optional gate that suppresses and removes the notification for this event */
  shouldDisplay?: (event: T) => boolean;
  /** The notification type this handler completes */
  type: NotificationType;
  /** Function to extract the notification ID from the event */
  getId: (event: T) => string;
  /** localStorage key to clear on completion */
  storageKey: string;
  /** Set for a type that owns one card per entity: the key then holds a record of card id to card. */
  storesCardsById?: boolean;
  /** Optional function to get the success message */
  getSuccessMessage?: (event: T, existing?: UnifiedNotification) => string;
  /** Optional function to get success details */
  getSuccessDetails?: (event: T, existing?: UnifiedNotification) => UnifiedNotification['details'];
  /** Optional function to get the cancelled message */
  getCancelledMessage?: (event: T, existing?: UnifiedNotification) => string;
  /** Optional function to get cancelled details */
  getCancelledDetails?: (
    event: T,
    existing?: UnifiedNotification
  ) => UnifiedNotification['details'];
  /**
   * Optional function to get detail message (shown below main message). An entry that does not
   * configure one leaves the card's existing detail line in place (see the fallbacks below). At a
   * TERMINAL an entry that DOES configure one owns the line outright, so returning undefined there
   * clears it - that is how a finished card drops the live byte counter it was showing while it ran.
   */
  getDetailMessage?: (event: T) => string | undefined;
  /** Optional function to get the failure message */
  getFailureMessage?: (event: T) => string;
  /**
   * Fixed outcome for an entry whose single event IS the whole lifecycle. Such a payload reports
   * no `success` field, because there was no run to report on, so the entry states what its event
   * always means.
   */
  succeeded?: boolean;
  /**
   * True where this event IS the whole lifecycle, so the entry has no started or progress phase.
   * Such an event replaces a terminal card in its slot instead of leaving it: with no other phase
   * there is no second operation of this type to confuse the card with, and whatever sits there
   * can only be an older announcement of the same kind.
   */
  announcement?: boolean;
  /** Auto-dismiss delay for this type's terminal card, where the shared default is wrong. */
  dismissDelayMs?: number;
  /** If true, show a brief animation delay before marking complete */
  useAnimationDelay?: boolean;
}

/**
 * Creates a handler function for completion events.
 * Completion handlers transition notifications from running to completed/failed.
 *
 * @template T - The type of the SignalR event (must have success: boolean)
 * @param config - Configuration for the handler
 * @param setNotifications - React setState function for notifications
 * @param scheduleAutoDismiss - Function to schedule auto-dismissal
 * @returns A handler function that processes the completion event
 *
 * @example
 * ```ts
 * const handleGameRemovalComplete = createCompletionHandler<GameRemovalCompleteEvent>(
 *   {
 *     type: 'game_removal',
 *     getId: () => NOTIFICATION_IDS.GAME_REMOVAL,
 *     storageKey: NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
 *     getSuccessDetails: (e) => ({ filesDeleted: e.filesDeleted })
 *   },
 *   setNotifications,
 *   scheduleAutoDismiss
 * );
 * ```
 */
export function createCompletionHandler<
  T extends {
    success: boolean;
    operationId?: string;
    error?: string;
    stageKey?: string;
    context?: Record<string, unknown>;
    message?: string;
    cancelled?: boolean;
    status?: string;
  }
>(
  config: CompletionHandlerConfig<T>,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): (event: T) => void {
  return (event: T): void => {
    const accepted =
      config.replay ||
      !config.events ||
      rememberEvent(config.events, config.type, 'complete', config.eventName ?? '', event);
    if (excludeChild(config, event, setNotifications) || !accepted) return;
    const operationId = eventOperationId(event);
    const notificationId = config.getId(event);
    const isCancelled = event.cancelled === true || event.status === 'cancelled';
    const isSkipped =
      !isCancelled &&
      (event.status === 'skipped' || (event as { skipped?: boolean }).skipped === true);
    const realError =
      typeof event.error === 'string' && event.error.trim() ? event.error : undefined;
    const succeeded =
      config.succeeded ?? (event.status !== 'failed' && event.success !== false && !realError);
    const status = isCancelled
      ? 'cancelled'
      : isSkipped
        ? 'skipped'
        : succeeded
          ? 'completed'
          : 'failed';
    const dismissDelayMs = isCancelled ? CANCELLED_NOTIFICATION_DELAY_MS : config.dismissDelayMs;

    setNotifications((prev) => {
      const exact = operationId
        ? prev.find((n) => n.type === config.type && n.details?.operationId === operationId)
        : undefined;
      const slot = prev.find((n) => n.id === notificationId);
      const existing =
        exact ??
        (slot && (config.announcement || eventTargetsCard(slot, event)) ? slot : undefined);
      const aggregate =
        config.type === 'corruption_removal' && (event as { service?: string }).service === 'all';
      if (aggregate && !exact) return prev;
      if (
        !existing &&
        slot &&
        (status !== 'failed' || !operationId || config.shouldDisplay?.(event) !== false)
      )
        return prev;
      if (existing && isTerminalNotificationStatus(existing.status) && !config.announcement)
        return prev;
      if (
        config.replay &&
        !existing &&
        operationId &&
        config.events?.terminals.get(operationId)?.presented
      )
        return prev;

      if (status !== 'failed' && config.shouldDisplay?.(event) === false) {
        if (!existing) return prev;
        clearPersistedNotificationIfTargeted(
          config.storageKey,
          event,
          existing.id,
          config.storesCardsById
        );
        return prev.filter((n) => n !== existing);
      }
      if (!existing && suppressNewItemCardDuringBulk(config.type, prev)) return prev;
      const id =
        existing?.id ?? (slot && operationId ? operationCardId(operationId) : notificationId);
      if (
        !clearPersistedNotificationIfTargeted(config.storageKey, event, id, config.storesCardsById)
      )
        return prev;
      const message = isCancelled
        ? (config.getCancelledMessage?.(event, existing) ??
          event.message ??
          (event.stageKey
            ? i18n.t(event.stageKey, event.context ?? {})
            : i18n.t(GENERIC_CANCELLED_I18N_KEY)))
        : status === 'failed'
          ? (realError ??
            config.getFailureMessage?.(event) ??
            (event.stageKey
              ? i18n.t(event.stageKey, event.context ?? {})
              : i18n.t(GENERIC_FAILURE_I18N_KEY)))
          : (config.getSuccessMessage?.(event, existing) ??
            (event.stageKey
              ? i18n.t(event.stageKey, event.context ?? {})
              : (existing?.message ?? i18n.t(GENERIC_COMPLETION_I18N_KEY))));
      const detailMessage = config.getDetailMessage
        ? config.getDetailMessage(event)
        : existing?.detailMessage;
      const details = {
        ...existing?.details,
        ...(isCancelled
          ? config.getCancelledDetails?.(event, existing)
          : config.getSuccessDetails?.(event, existing)),
        ...(operationId ? { operationId } : {}),
        ...(isCancelled ? { cancelled: true } : {})
      };
      delete details.cancelPending;
      delete details.cancelling;
      const terminal: UnifiedNotification = {
        ...existing,
        id,
        type: config.type,
        status,
        controlOnly: undefined,
        message,
        error: status === 'failed' ? message : undefined,
        detailMessage:
          config.type === 'eviction_scan'
            ? (detailMessage ?? existing?.detailMessage)
            : detailMessage,
        startedAt: existing?.startedAt ?? new Date(),
        instanceVersion: existing?.instanceVersion ?? 1,
        progress: isSkipped ? undefined : FULL_PROGRESS_PERCENT,
        details
      };
      scheduleAutoDismiss(id, dismissDelayMs);
      return existing ? prev.map((n) => (n === existing ? terminal : n)) : [...prev, terminal];
    });
  };
}

// ============================================================================
// Status-Aware Progress Handler Factory
// ============================================================================

/**
 * Configuration for creating a status-aware progress handler.
 * This handler automatically detects completion/error states from the event's status field.
 * @template T - The type of the SignalR event (must have optional status field)
 */
interface StatusAwareProgressConfig<T> extends NotificationEventOptions {
  canControl?: (event: T) => boolean;
  /** Optional gate that suppresses and removes the notification for this event */
  shouldDisplay?: (event: T) => boolean;
  /** The notification type this handler updates */
  type: NotificationType;
  /** Function to extract the notification ID from the event */
  getId: (event: T) => string;
  /** localStorage key for persisting/clearing the notification */
  storageKey: string;
  /** Set for a type that owns one card per entity: the key then holds a record of card id to card. */
  storesCardsById?: boolean;
  /** Function to get the progress message */
  getMessage: (event: T) => string;
  /** Function to get progress percentage (0-100) */
  getProgress: (event: T) => number | undefined;
  /** Optional secondary metrics shown below the stable primary message. */
  getDetailMessage?: (event: T) => string | undefined;
  /** Optional phase-aware progress semantics. */
  getProgressMode?: (event: T) => NotificationProgressMode | undefined;
  /** Optional textual equivalent of the progress metrics. */
  getProgressAriaValueText?: (event: T) => string | undefined;
  /** Function to get the status from the event */
  getStatus: (event: T) => string | undefined;
  /** Message to show on completion (can use event data) */
  getCompletedMessage?: (event: T) => string;
  /** Message to show on error (uses event message by default) */
  getErrorMessage?: (event: T) => string | undefined;
  /** If true, support fast completion (completion event arrives before notification created) */
  supportFastCompletion?: boolean;
  /** Optional function to get notification details from the event (e.g., operationId for cancel support) */
  getDetails?: (event: T) => UnifiedNotification['details'];
}

/**
 * Creates a handler function for progress events that automatically handles
 * completion and error states based on the event's status field.
 *
 * This is useful for events that use a single progress handler for all states
 * (started, progress, completed, error) rather than separate handlers.
 *
 * @template T - The type of the SignalR event
 * @param config - Configuration for the handler
 * @param setNotifications - React setState function for notifications
 * @param scheduleAutoDismiss - Function to schedule auto-dismissal
 * @param cancelAutoDismissTimer - Optional function to cancel pending auto-dismiss
 * @returns A handler function that processes the progress event
 *
 * @example
 * ```ts
 * const handleDatabaseResetProgress = createStatusAwareProgressHandler<DatabaseResetProgressEvent>(
 *   {
 *     type: 'database_reset',
 *     getId: () => NOTIFICATION_IDS.DATABASE_RESET,
 *     storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
 *     getMessage: (e) => e.message || 'Resetting database...',
 *     getProgress: (e) => e.percentComplete || 0,
 *     getStatus: (e) => e.status,
 *     getCompletedMessage: (e) => e.message || 'Database reset completed'
 *   },
 *   setNotifications,
 *   scheduleAutoDismiss,
 *   cancelAutoDismissTimer
 * );
 * ```
 */
/** @public */
export function createStatusAwareProgressHandler<T>(
  config: StatusAwareProgressConfig<T>,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer?: CancelAutoDismissTimer
): (event: T) => void {
  return (event: T): void => {
    const accepted =
      config.replay ||
      !config.events ||
      rememberEvent(config.events, config.type, 'progress', config.eventName ?? '', event);
    if (excludeChild(config, event, setNotifications) || !accepted) return;
    const operationId = eventOperationId(event);
    const status = config.getStatus(event)?.toLowerCase();
    if (status && ['completed', 'cancelled', 'skipped', 'failed'].includes(status)) {
      createCompletionHandler(
        {
          type: config.type,
          events: config.events,
          replay: true,
          eventName: config.eventName,
          getId: () => config.getId(event),
          storageKey: config.storageKey,
          storesCardsById: config.storesCardsById,
          shouldDisplay: () => config.shouldDisplay?.(event) !== false,
          getSuccessMessage: () => config.getCompletedMessage?.(event) ?? config.getMessage(event),
          getFailureMessage: () =>
            config.getErrorMessage?.(event) ?? i18n.t(GENERIC_FAILURE_I18N_KEY),
          getSuccessDetails: () => config.getDetails?.(event),
          getDetailMessage: config.getDetailMessage
            ? () => config.getDetailMessage?.(event)
            : undefined
        },
        setNotifications,
        scheduleAutoDismiss
      )({
        ...(event as object),
        success: status === 'completed' || status === 'skipped',
        cancelled: status === 'cancelled',
        status,
        operationId,
        error: (event as { error?: string }).error
      });
      return;
    }
    if (operationId && config.events?.terminals.has(operationId)) return;
    const notificationId = config.getId(event);
    setNotifications((prev) => {
      const exact = operationId
        ? prev.find((n) => n.type === config.type && n.details?.operationId === operationId)
        : undefined;
      const slot = prev.find((n) => n.id === notificationId);
      const existing = exact ?? (slot && eventTargetsCard(slot, event) ? slot : undefined);
      if (existing && isTerminalNotificationStatus(existing.status)) return prev;
      const hidden = config.shouldDisplay?.(event) === false;
      if (!existing && slot && !hidden) return prev;
      if (hidden && !config.canControl?.(event)) {
        if (!existing) return prev;
        clearPersistedNotificationIfTargeted(
          config.storageKey,
          event,
          existing.id,
          config.storesCardsById
        );
        cancelAutoDismissTimer?.(existing.id);
        return prev.filter((n) => n !== existing);
      }
      if (hidden && !operationId) return prev;
      if (operationId && findBulkCardOwningOperation(config.type, operationId, prev)) return prev;
      if (!existing && suppressNewItemCardDuringBulk(config.type, prev)) return prev;
      const id =
        existing?.id ?? (hidden && operationId ? operationCardId(operationId) : notificationId);
      if (!existing) cancelAutoDismissTimer?.(id);
      const detailMessage = config.getDetailMessage?.(event);
      const card: UnifiedNotification = {
        ...existing,
        id,
        type: config.type,
        status: existing?.details?.cancelRequested
          ? 'cancelling'
          : promoteStatus(existing?.status ?? 'running'),
        controlOnly: hidden || undefined,
        message: config.getMessage(event),
        progress: config.getProgress(event),
        ...(config.getDetailMessage && {
          detailMessage:
            config.type === 'eviction_scan'
              ? (detailMessage ?? existing?.detailMessage)
              : detailMessage
        }),
        ...(config.getProgressMode && { progressMode: config.getProgressMode(event) }),
        ...(config.getProgressAriaValueText && {
          progressAriaValueText: config.getProgressAriaValueText(event)
        }),
        startedAt: existing?.startedAt ?? new Date(),
        instanceVersion: existing?.instanceVersion ?? 1,
        details: mergeEventDetails(existing?.details, {
          ...config.getDetails?.(event),
          ...(operationId ? { operationId } : {})
        })
      };
      persistNotification(config.storageKey, card, config.storesCardsById);
      return existing ? prev.map((n) => (n === existing ? card : n)) : [...prev, card];
    });
  };
}

function buildStartedHandler(
  entry: NotificationRegistryEntry,
  started: NonNullable<NotificationRegistryEntry['started']>,
  setNotifications: SetNotifications,
  cancelAutoDismissTimer: CancelAutoDismissTimer,
  events?: NotificationEvents,
  replay = false,
  scheduleAutoDismiss: ScheduleAutoDismiss = () => undefined
): (event: unknown) => void {
  const create = (set: SetNotifications) =>
    createStartedHandler(
      {
        type: entry.type,
        events,
        replay,
        getId: (event: unknown) => entry.getId?.(event) ?? entry.id,
        storageKey: entry.storageKey,
        storesCardsById: entry.getId !== undefined,
        shouldDisplay: started.shouldDisplay,
        canControl: (event: unknown) => {
          const fields = event as { operationId?: unknown; serviceId?: unknown };
          return (
            entry.cancelKind !== 'none' &&
            entry.cancelKind !== 'clientQueue' &&
            typeof fields.operationId === 'string' &&
            (entry.type !== 'scheduled_prefill' || typeof fields.serviceId === 'string')
          );
        },
        eventName: entry.events?.started,
        defaultMessage: started.defaultMessage,
        getMessage: started.getMessage,
        getDetails: (event: unknown) => ({
          ...started.getDetails?.(event),
          ...(entry.type === 'scheduled_prefill'
            ? {
                service: (event as { serviceId?: string }).serviceId,
                operationId: (event as { operationId?: string }).operationId
              }
            : {})
        }),
        replaceExisting: started.replaceExisting,
        progressMode: started.progressMode
      },
      set,
      cancelAutoDismissTimer
    );
  if (replay || !events) return create(setNotifications);
  return (event) => {
    setNotifications((prev) => {
      let next = prev;
      create((update) => {
        next = typeof update === 'function' ? update(next) : update;
      })(event);
      return applyPredecessor(
        next,
        event,
        events,
        entry,
        scheduleAutoDismiss,
        cancelAutoDismissTimer
      );
    });
  };
}

export function applyPredecessor(
  prev: UnifiedNotification[],
  event: unknown,
  events: NotificationEvents,
  entry: NotificationRegistryEntry,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer: CancelAutoDismissTimer = () => undefined
): UnifiedNotification[] {
  const previousOperationId = (event as { previousOperationId?: unknown } | null)
    ?.previousOperationId;
  const operationId = eventOperationId(event);
  if (
    typeof previousOperationId !== 'string' ||
    !previousOperationId.trim() ||
    !operationId ||
    previousOperationId === operationId
  )
    return prev;
  const handoff: OperationWaitingCompleteEvent = {
    operationId: previousOperationId,
    operationType: entry.type,
    nextOperationId: operationId,
    promoted: true,
    cancelled: false
  };
  rememberEvent(events, entry.type, 'handoff', 'OperationWaitingComplete', handoff);
  return applyHandoff(prev, handoff, events, entry, scheduleAutoDismiss, cancelAutoDismissTimer);
}

/**
 * Creates a status-aware progress handler for a registry entry.
 */
function buildProgressHandler(
  entry: NotificationRegistryEntry,
  progress: NonNullable<NotificationRegistryEntry['progress']>,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer: CancelAutoDismissTimer,
  events?: NotificationEvents,
  replay = false
): (event: unknown) => void {
  return createStatusAwareProgressHandler(
    {
      type: entry.type,
      events,
      replay,
      getId: (event: unknown) => entry.getId?.(event) ?? entry.id,
      storageKey: entry.storageKey,
      storesCardsById: entry.getId !== undefined,
      shouldDisplay: progress.shouldDisplay,
      canControl: (event: unknown) => {
        const fields = event as { operationId?: unknown; serviceId?: unknown };
        return (
          entry.cancelKind !== 'none' &&
          entry.cancelKind !== 'clientQueue' &&
          typeof fields.operationId === 'string' &&
          (entry.type !== 'scheduled_prefill' || typeof fields.serviceId === 'string')
        );
      },
      eventName: entry.events?.progress,
      getMessage: progress.getMessage,
      getProgress: progress.getProgress,
      getDetailMessage: progress.getDetailMessage,
      getProgressMode: progress.getProgressMode,
      getProgressAriaValueText: progress.getProgressAriaValueText,
      getStatus: progress.getStatus,
      getCompletedMessage: progress.getCompletedMessage,
      getErrorMessage: progress.getErrorMessage,
      supportFastCompletion: progress.supportFastCompletion,
      getDetails: (event: unknown) => ({
        ...progress.getDetails?.(event),
        ...(entry.type === 'scheduled_prefill'
          ? {
              service: (event as { serviceId?: string }).serviceId,
              operationId: (event as { operationId?: string }).operationId
            }
          : {})
      })
    },
    setNotifications,
    scheduleAutoDismiss,
    cancelAutoDismissTimer
  );
}

/**
 * Creates a completion handler using the entry's existing lifecycle contract.
 */
function buildCompleteHandler(
  entry: NotificationRegistryEntry,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  events?: NotificationEvents,
  replay = false
): ((event: unknown) => void) | null {
  if (!entry.complete) return null;

  const baseHandler = createCompletionHandler(
    {
      type: entry.type,
      events,
      replay,
      getId: (event: unknown) => entry.getId?.(event) ?? entry.id,
      storageKey: entry.storageKey,
      storesCardsById: entry.getId !== undefined,
      eventName: entry.events?.complete,
      shouldDisplay: entry.complete.shouldDisplay,
      getSuccessMessage: entry.complete.getSuccessMessage,
      getSuccessDetails: entry.complete.getSuccessDetails,
      getDetailMessage: entry.complete.getDetailMessage,
      getFailureMessage: entry.complete.getFailureMessage,
      getCancelledMessage: entry.complete.getCancelledMessage,
      getCancelledDetails: entry.complete.getCancelledDetails,
      succeeded: entry.complete.succeeded,
      announcement: !entry.started && !entry.progress,
      dismissDelayMs: entry.complete.dismissDelayMs,
      useAnimationDelay: entry.complete.useAnimationDelay
    },
    setNotifications,
    scheduleAutoDismiss
  );

  return (event: unknown) => baseHandler(event as Parameters<typeof baseHandler>[0]);
}

export { buildStartedHandler, buildProgressHandler, buildCompleteHandler };

/** Apply only the recorded waiter relationship, preserving its logical card slot. */
export function applyHandoff(
  prev: UnifiedNotification[],
  event: OperationWaitingCompleteEvent,
  events: NotificationEvents,
  entry: NotificationRegistryEntry,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer: CancelAutoDismissTimer = () => undefined
): UnifiedNotification[] {
  const confirmed = events.handoffs.get(event.operationId);
  if (
    !confirmed ||
    confirmed.promoted !== event.promoted ||
    confirmed.nextOperationId !== event.nextOperationId
  )
    return prev;
  const waiting = prev.find(
    (n) => n.type === entry.type && n.details?.operationId === event.operationId
  );
  if (!event.promoted) {
    prev = prev.map((n) =>
      n.status === 'waiting' && n.details?.currentOperationId === event.operationId
        ? { ...n, status: 'running' }
        : n
    );
    const status = event.cancelled ? 'cancelled' : event.skipped ? 'skipped' : 'failed';
    if (!waiting) {
      if (status !== 'failed' || events.terminals.get(event.operationId)?.presented) return prev;
      const id = operationCardId(event.operationId);
      const message = event.error ?? i18n.t(GENERIC_FAILURE_I18N_KEY);
      scheduleAutoDismiss(id);
      return [
        ...prev,
        {
          id,
          type: entry.type,
          status,
          message,
          error: message,
          startedAt: new Date(),
          details: { operationId: event.operationId }
        }
      ];
    }
    if (isTerminalNotificationStatus(waiting.status)) return prev;
    clearPersistedNotificationIfTargeted(
      entry.storageKey,
      event,
      waiting.id,
      entry.getId !== undefined
    );
    if (waiting.controlOnly && status !== 'failed') return prev.filter((n) => n !== waiting);
    const message = event.cancelled
      ? i18n.t('common.notifications.operationWaitingCancelled')
      : (event.error ??
        i18n.t(status === 'skipped' ? GENERIC_SKIPPED_I18N_KEY : GENERIC_FAILURE_I18N_KEY));
    scheduleAutoDismiss(waiting.id);
    return prev.map((n) =>
      n === waiting
        ? {
            ...n,
            status,
            message,
            controlOnly: undefined,
            error: status === 'failed' ? message : undefined,
            details: { ...n.details, cancelled: event.cancelled, cancelPending: false }
          }
        : n
    );
  }
  const nextOperationId = event.nextOperationId;
  if (!nextOperationId || nextOperationId === event.operationId) return prev;
  const target = prev.find(
    (n) => n.type === entry.type && n.details?.operationId === nextOperationId
  );
  let next = prev.map((n) =>
    n.details?.currentOperationId === event.operationId
      ? {
          ...n,
          status: n.status === 'waiting' ? ('running' as const) : n.status,
          details: { ...n.details, currentOperationId: nextOperationId }
        }
      : n
  );
  if (!waiting && !target) return next;
  const terminal = events.terminals.get(nextOperationId);
  if (terminal?.presented && !target) {
    if (waiting) {
      clearPersistedNotificationIfTargeted(
        entry.storageKey,
        event,
        waiting.id,
        entry.getId !== undefined
      );
      cancelAutoDismissTimer(waiting.id);
    }
    return next.filter((n) => n !== waiting);
  }
  const older = !waiting
    ? target!
    : !target
      ? waiting
      : waiting.startedAt.getTime() < target.startedAt.getTime() ||
          (waiting.startedAt.getTime() === target.startedAt.getTime() && waiting.id < target.id)
        ? waiting
        : target;
  const sampledStatus = event.nextStatus;
  const status: NotificationStatus =
    target?.status ??
    (sampledStatus === 'running' || sampledStatus === 'waiting' || sampledStatus === 'cancelling'
      ? sampledStatus
      : 'pending');
  const card: UnifiedNotification = {
    ...waiting,
    ...target,
    id: older.id,
    type: entry.type,
    startedAt: older.startedAt,
    instanceVersion: older.instanceVersion,
    status,
    message: target?.message ?? '',
    details: {
      ...waiting?.details,
      ...target?.details,
      operationId: nextOperationId,
      cancelRequested: waiting?.details?.cancelRequested || target?.details?.cancelRequested,
      cancelSent: waiting?.details?.cancelSent || target?.details?.cancelSent,
      cancelPending: false
    }
  };
  if (card.details?.cancelRequested && !isTerminalNotificationStatus(card.status))
    card.status = 'cancelling';
  if (waiting)
    clearPersistedNotificationIfTargeted(
      entry.storageKey,
      event,
      waiting.id,
      entry.getId !== undefined
    );
  for (const old of [waiting, target]) {
    if (old && old.id !== card.id) cancelAutoDismissTimer(old.id);
  }
  next = next.flatMap((n) => (n === older ? [card] : n === waiting || n === target ? [] : [n]));
  const retained = events.records.get(nextOperationId);
  const setNotifications: SetNotifications = (update) => {
    next = typeof update === 'function' ? update(next) : update;
  };
  if (!target || !isTerminalNotificationStatus(target.status)) {
    if (retained?.started && entry.started)
      buildStartedHandler(
        entry,
        entry.started,
        setNotifications,
        cancelAutoDismissTimer,
        events,
        true
      )(retained.started.body);
    if (retained?.progress && entry.progress)
      buildProgressHandler(
        entry,
        entry.progress,
        setNotifications,
        scheduleAutoDismiss,
        cancelAutoDismissTimer,
        events,
        true
      )(retained.progress.body);
    if (retained?.complete)
      buildCompleteHandler(
        entry,
        setNotifications,
        scheduleAutoDismiss,
        events,
        true
      )?.(retained.complete.body);
  }
  const knownStatus =
    terminal?.status ??
    (sampledStatus === 'completed' ||
    sampledStatus === 'failed' ||
    sampledStatus === 'cancelled' ||
    sampledStatus === 'skipped'
      ? sampledStatus
      : undefined);
  if (knownStatus) {
    next = next.map((n) => {
      if (n.id !== card.id || isTerminalNotificationStatus(n.status)) return n;
      const message =
        terminal?.error ??
        event.error ??
        i18n.t(
          knownStatus === 'failed'
            ? GENERIC_FAILURE_I18N_KEY
            : knownStatus === 'cancelled'
              ? GENERIC_CANCELLED_I18N_KEY
              : knownStatus === 'skipped'
                ? GENERIC_SKIPPED_I18N_KEY
                : GENERIC_COMPLETION_I18N_KEY
        );
      scheduleAutoDismiss(n.id);
      return {
        ...n,
        status: knownStatus,
        message,
        controlOnly: knownStatus === 'failed' ? undefined : n.controlOnly,
        error: knownStatus === 'failed' ? message : undefined,
        progress: knownStatus === 'skipped' ? undefined : FULL_PROGRESS_PERCENT
      };
    });
    if (!terminal)
      events.terminals.set(nextOperationId, {
        operationId: nextOperationId,
        status: knownStatus,
        error: event.error
      });
  }
  const applied = next.find((n) => n.id === card.id);
  if (applied) {
    if (isTerminalNotificationStatus(applied.status)) {
      clearPersistedNotificationIfTargeted(
        entry.storageKey,
        { operationId: nextOperationId },
        applied.id,
        entry.getId !== undefined
      );
      if (target && isTerminalNotificationStatus(target.status) && target.id !== applied.id)
        scheduleAutoDismiss(applied.id);
    } else persistNotification(entry.storageKey, applied, entry.getId !== undefined);
  }
  return next;
}
