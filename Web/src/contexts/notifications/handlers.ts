/**
 * The per-type event handlers behind every run card's text.
 *
 * A run card opens and ends on the server's run rows (`runStore.ts`). What a card SAYS comes from
 * the per-type lifecycle events: each registry entry's started, progress and completion getters
 * turn an event into a detail patch (message, detail line, percent, details), and these builders
 * hand that patch to the store through `dispatchDetail`. An announcement - an entry whose single
 * event is the whole story - becomes a card the browser owns instead.
 *
 * Where to look for the rest of a card's life:
 *   - a batch's OWN card is created in `BulkRemovalContext`, and its run loop is
 *     `hooks/useBatchQueue.ts`; its items fold into it in `runStore.ts`
 *   - detail for a card seen first after a reload or a reconnect comes from `recovery.ts`
 *   - what a card looks like is decided in `UniversalNotificationBar`, via `utils/statusVariant`
 */

import type {
  DispatchDetail,
  NotificationRegistryEntry,
  NotificationType,
  RegistryCompleteConfig,
  UnifiedNotification
} from './types';
import type { RunDetail } from './runStore';
import i18n from '@/i18n';
import {
  FULL_PROGRESS_PERCENT,
  GENERIC_CANCELLED_I18N_KEY,
  GENERIC_COMPLETION_I18N_KEY,
  GENERIC_FAILURE_I18N_KEY,
  OPERATION_WAITING_I18N_KEYS
} from './constants';

/** The fields a terminal event can carry; every one is optional on the wire. */
interface TerminalEvent {
  success?: boolean;
  error?: string;
  stageKey?: string;
  context?: Record<string, unknown>;
  message?: string;
  cancelled?: boolean;
  skipped?: boolean;
  status?: string;
}

/** operationId carried by any lifecycle event on the wire. */
function eventOperationId(event: unknown): string | undefined {
  const operationId = (event as { operationId?: unknown } | null | undefined)?.operationId;
  return typeof operationId === 'string' ? operationId : undefined;
}

/**
 * Text for an operation parked in the wait queue. Every waiting card - a run's own, and a batch
 * card whose item is parked - words it here from the run row's name and blocker, so the sentence
 * never changes when a reconnect swaps one source for another.
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

/**
 * What a finished run's card says, from its completion getters. The run row decides the card's
 * status and how long it stays; this is only its text, so a completion that lands after the row
 * still gives the card its summary and translated terminal line.
 */
function terminalDetail(
  type: NotificationType,
  complete: RegistryCompleteConfig,
  event: TerminalEvent,
  existing: UnifiedNotification | undefined
): RunDetail & { message: string } {
  const isCancelled = event.cancelled === true || event.status === 'cancelled';
  const isSkipped = !isCancelled && (event.status === 'skipped' || event.skipped === true);
  const realError = typeof event.error === 'string' && event.error.trim() ? event.error : undefined;
  const succeeded =
    complete.succeeded ?? (event.status !== 'failed' && event.success !== false && !realError);
  const failed = !isCancelled && !isSkipped && !succeeded;
  const message = isCancelled
    ? complete.getCancelledMessage
      ? complete.getCancelledMessage(event, existing)
      : (event.message ??
        (event.stageKey
          ? i18n.t(event.stageKey, event.context ?? {})
          : i18n.t(GENERIC_CANCELLED_I18N_KEY)))
    : failed
      ? (realError ??
        (complete.getFailureMessage
          ? complete.getFailureMessage(event)
          : event.stageKey
            ? i18n.t(event.stageKey, event.context ?? {})
            : i18n.t(GENERIC_FAILURE_I18N_KEY)))
      : complete.getSuccessMessage
        ? complete.getSuccessMessage(event, existing)
        : event.stageKey
          ? i18n.t(event.stageKey, event.context ?? {})
          : (existing?.message ?? i18n.t(GENERIC_COMPLETION_I18N_KEY));
  const detail: RunDetail & { message: string } = {
    message,
    error: failed ? message : undefined,
    progress: isSkipped ? undefined : FULL_PROGRESS_PERCENT,
    details: {
      ...(isCancelled
        ? complete.getCancelledDetails?.(event, existing)
        : complete.getSuccessDetails?.(event, existing)),
      ...(isCancelled ? { cancelled: true } : {})
    }
  };
  if (complete.getDetailMessage) {
    const line = complete.getDetailMessage(event);
    // An eviction scan's line is the warning its detection phase left; a terminal with nothing to
    // add keeps it rather than blanking it.
    detail.detailMessage = type === 'eviction_scan' ? (line ?? existing?.detailMessage) : line;
  }
  return detail;
}

/** Builds the handler for an entry's Started event: the card's first line and details. */
function buildStartedHandler(
  started: NonNullable<NotificationRegistryEntry['started']>,
  dispatchDetail: DispatchDetail
): (event: unknown) => void {
  return (event: unknown): void => {
    const operationId = eventOperationId(event);
    if (!operationId) return;
    dispatchDetail(
      operationId,
      () => ({
        message: started.getMessage?.(event) || started.defaultMessage,
        details: started.getDetails?.(event),
        ...(started.progressMode ? { progressMode: started.progressMode } : {})
      }),
      'event'
    );
  };
}

/**
 * Builds the handler for an entry's Progress event. A progress event that reports a terminal status
 * is the run's completion for the pipelines that end that way.
 */
function buildProgressHandler(
  entry: NotificationRegistryEntry,
  progress: NonNullable<NotificationRegistryEntry['progress']>,
  dispatchDetail: DispatchDetail
): (event: unknown) => void {
  return (event: unknown): void => {
    const operationId = eventOperationId(event);
    if (!operationId) return;
    const status = progress.getStatus(event)?.toLowerCase();
    if (
      status === 'completed' ||
      status === 'failed' ||
      status === 'cancelled' ||
      status === 'skipped'
    ) {
      dispatchDetail(
        operationId,
        (existing) =>
          terminalDetail(
            entry.type,
            {
              getSuccessMessage: () =>
                progress.getCompletedMessage
                  ? progress.getCompletedMessage(event)
                  : progress.getMessage(event),
              getFailureMessage: () =>
                progress.getErrorMessage?.(event) ?? i18n.t(GENERIC_FAILURE_I18N_KEY),
              getSuccessDetails: () => progress.getDetails?.(event),
              getDetailMessage: progress.getDetailMessage
                ? () => progress.getDetailMessage?.(event)
                : undefined
            },
            {
              ...(event as TerminalEvent),
              success: status === 'completed' || status === 'skipped',
              cancelled: status === 'cancelled',
              status
            },
            existing
          ),
        'completion'
      );
      return;
    }
    const stage = (event as { stage?: string }).stage;
    // A prefill stage change that carries no new line (recovering, cancelling, or running before
    // its first stage key) keeps the card's message, percent and detail line.
    const transitionOnly =
      entry.type === 'scheduled_prefill' &&
      (stage === 'recovering' ||
        stage === 'cancelling' ||
        (stage === 'running' &&
          !(event as { stageKey?: string }).stageKey &&
          progress.getProgress(event) == null));
    if (transitionOnly) {
      dispatchDetail(operationId, () => ({ details: progress.getDetails?.(event) }), 'event');
      return;
    }
    dispatchDetail(
      operationId,
      () => {
        const patch: RunDetail = {
          message: progress.getMessage(event),
          progress: progress.getProgress(event),
          details: progress.getDetails?.(event)
        };
        if (progress.getDetailMessage) {
          const line = progress.getDetailMessage(event);
          // An eviction scan's detail line is its detection warning, which later ticks do not repeat.
          if (entry.type !== 'eviction_scan' || line !== undefined) patch.detailMessage = line;
        }
        if (progress.getProgressMode) patch.progressMode = progress.getProgressMode(event);
        if (progress.getProgressAriaValueText)
          patch.progressAriaValueText = progress.getProgressAriaValueText(event);
        return patch;
      },
      'event'
    );
  };
}

/** Builds the handler for an entry's completion event: the finished card's text. */
function buildCompleteHandler(
  entry: NotificationRegistryEntry,
  complete: RegistryCompleteConfig,
  dispatchDetail: DispatchDetail
): (event: unknown) => void {
  return (event: unknown): void => {
    const operationId = eventOperationId(event);
    if (!operationId) return;
    dispatchDetail(
      operationId,
      (existing) => terminalDetail(entry.type, complete, event as TerminalEvent, existing),
      'completion'
    );
  };
}

/**
 * Builds the handler for an announcement: an entry whose single event is the whole story (a catalog
 * update, a dropped Steam session). It is no run, so it raises a card the browser owns.
 */
function buildAnnouncementHandler(
  entry: NotificationRegistryEntry,
  complete: RegistryCompleteConfig,
  showAnnouncement: (card: Omit<UnifiedNotification, 'id' | 'startedAt'>) => void
): (event: unknown) => void {
  return (event: unknown): void => {
    if (complete.shouldDisplay?.(event) === false) return;
    showAnnouncement({
      ...terminalDetail(entry.type, complete, event as TerminalEvent, undefined),
      type: entry.type,
      status: complete.succeeded ? 'completed' : 'failed'
    });
  };
}

export {
  buildStartedHandler,
  buildProgressHandler,
  buildCompleteHandler,
  buildAnnouncementHandler
};
