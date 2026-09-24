/**
 * The run store: every server-tracked operation this browser knows about, as plain state and pure
 * reducers with no React in them, so the notification context and its tests run the same code.
 *
 * A run card opens when a run row (the `OperationUpdated` push or the `GET /api/operations/runs`
 * snapshot) says the run exists, and it ends when a row says the run ended. The per-type lifecycle
 * events only fill in the text, detail line and percent a card shows (`applyDetail`). Nothing here
 * runs on a timer: once a row decided a card leaves, the provider shows it for the one popup time
 * and then plays its 300 ms fade.
 *
 * One card per run, keyed by the server's operation id. A run that hands its work to another
 * operation (a queued run promoted onto a new id, a held scan continuing as a new run) merges into
 * that operation's entry, so the card keeps its id and its place while `details.operationId`
 * follows the work.
 */

import i18n from '@/i18n';
import {
  isIsoDate,
  isOptionalNonNegativeInteger,
  isPlainRecord
} from '@components/features/management/cache/corruptionContractValidation';
import { SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY } from '@components/features/management/schedules/scheduled-prefill/constants';
import { translateRecoveryStage, translateStageKeyMessage } from '@utils/stageKeyMessage';
import { formatCount } from '@utils/formatters';
import type { OperationStatus } from '../../types/operations';
import type { OperationRun, OperationRunsSnapshot, RunVisibility } from '../SignalRContext/types';
import type {
  NotificationProgressMode,
  NotificationTerminal,
  NotificationType,
  UnifiedNotification
} from './types';
import {
  GENERIC_CANCELLED_I18N_KEY,
  GENERIC_COMPLETION_I18N_KEY,
  GENERIC_FAILURE_I18N_KEY,
  GENERIC_SKIPPED_I18N_KEY,
  type LIVE_ONLY_CANCEL_DETAIL_KEYS,
  OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE,
  SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY
} from './constants';
import { isTerminalNotificationStatus } from './notificationStatus';
import { waitingCardMessage } from './handlers';
import { detectionErrorDetail } from './detailMessageFormatters';

/** The text, detail line and percent that per-type events or detail recovery supplied for a run. */
export interface RunDetail {
  message?: string;
  detailMessage?: string;
  progress?: number;
  progressMode?: NotificationProgressMode;
  progressAriaValueText?: string;
  error?: string;
  details?: UnifiedNotification['details'];
}

/**
 * Scheduled prefill's event ordering. Its daemon numbers events within a series (`eventEpoch` on the
 * wire); a restarted daemon starts a new series, and the run-status response names the current one.
 */
interface RunStream {
  series: string;
  sequence: number;
  daemonInstanceId?: string;
  retired: Set<string>;
  /** The one patch from a series not yet adopted, held until detail recovery names the series. */
  pending?: { series: string; sequence: number; detail: RunDetail; source: 'event' | 'completion' };
}

export interface RunEntry {
  run: OperationRun;
  /** The first operation id of the handoff chain; the drawn card's id. */
  cardId: string;
  /** Every operation id merged into this entry, its own included, oldest first. */
  aliases: string[];
  startedAt: Date;
  /** The connection generation the last accepted row arrived in. */
  generation: number;
  /** How the last accepted row says to show this run. */
  visibility: RunVisibility;
  detail: RunDetail;
  /** Accepted event patches; 0 means the card still shows only what its row says. */
  detailRevision: number;
  /** Request number of the last detail recovery applied to this entry. */
  recoverySeq: number;
  /** The provider's last request number when the last event or completion patch was accepted. */
  lastEventSeq: number;
  /** The message a predecessor showed, kept until this run's own detail arrives. */
  carriedMessage?: string;
  cancel: Pick<
    NonNullable<UnifiedNotification['details']>,
    (typeof LIVE_ONLY_CANCEL_DETAIL_KEYS)[number]
  >;
  /** Stays until closed: a kept ending, or a success kept by Keep Notifications Visible. */
  retained: boolean;
  /** Ended and fading out. */
  leaving: boolean;
  connectionRecovering?: boolean;
  /**
   * Closed on this screen while the server was unreachable. Only the drawn cards skip it: the run
   * stays busy and its waiters keep waiting. The next accepted row or progress detail for the run
   * clears it.
   */
  hiddenHere?: boolean;
  stream?: RunStream;
}

interface EndedRecord {
  revision: number;
  generation: number;
  status: NotificationTerminal['status'];
  /** The entry this run merged into; a waiter follows it instead of reading `status`. */
  mergedInto?: string;
  /** Request number issued when a snapshot first missed this id; see `applySnapshot`. */
  absentSeq?: number;
}

export interface RunStoreState {
  /** Keyed by each entry's current operation id. */
  entries: Map<string, RunEntry>;
  /** Detail for a run whose row has not arrived yet. */
  buffer: Map<string, RunDetail>;
  /** Runs that left the store, so a delayed or repeated row never brings a card back. */
  ended: Map<string, EndedRecord>;
  /** Successor id -> predecessor ids of handoffs whose successor has no entry yet. */
  links: Map<string, string[]>;
  generation: number;
  /** The highest revision of a snapshot applied in the current generation. */
  snapshotRevision: number;
  /** Bulk cards that have owned a kept run at some point. */
  keptBatches: Set<string>;
}

export interface RunApplyResult {
  next: RunStoreState;
  /** Runs this apply ended, for the waiters on them. */
  ended: NotificationTerminal[];
  /** Runs merged into another, so a waiter on `from` follows `to`. */
  merged: { from: string; to: string }[];
}

interface RunApplyOptions {
  keepSuccessVisible: boolean;
  localCards: readonly UnifiedNotification[];
  /** True for an `OperationUpdated` push, false for a snapshot row. */
  pushed: boolean;
  /** This browser's auth session; null when signed out. */
  sessionId: string | null;
}

const OPERATION_STATUSES: readonly OperationStatus[] = [
  'pending',
  'running',
  'waiting',
  'cancelling',
  'completed',
  'failed',
  'cancelled',
  'skipped'
];
// Every visibility a row may carry, for the wire check.
const VISIBILITY_ORDER: readonly RunVisibility[] = ['hidden', 'background', 'card'];

const isNonEmptyString = (value: unknown): boolean => typeof value === 'string' && value.length > 0;
const isOptionalText = (value: unknown): boolean =>
  value === undefined || value === null || typeof value === 'string';
const isOptionalId = (value: unknown): boolean =>
  value === undefined || value === null || isNonEmptyString(value);
const isOptionalFlag = (value: unknown): boolean =>
  value === undefined || typeof value === 'boolean';
const isRevision = (value: unknown): boolean =>
  Number.isSafeInteger(value) && (value as number) >= 1;

/**
 * Validates one run row from the wire. A malformed row is dropped and logged, never drawn.
 */
export function readOperationRun(value: unknown): OperationRun | null {
  if (
    isPlainRecord(value) &&
    isNonEmptyString(value.operationId) &&
    isNonEmptyString(value.operationType) &&
    isNonEmptyString(value.name) &&
    OPERATION_STATUSES.includes(value.status as OperationStatus) &&
    VISIBILITY_ORDER.includes(value.visibility as RunVisibility) &&
    typeof value.message === 'string' &&
    Number.isFinite(value.percentComplete) &&
    isRevision(value.revision) &&
    isIsoDate(value.startedAt) &&
    isOptionalText(value.error) &&
    isOptionalText(value.blockedByName) &&
    isOptionalText(value.warning) &&
    isOptionalId(value.previousOperationId) &&
    isOptionalId(value.parentOperationId) &&
    isOptionalId(value.nextOperationId) &&
    isOptionalId(value.scheduleId) &&
    isOptionalId(value.serviceId) &&
    isOptionalId(value.ownerSessionId) &&
    isOptionalFlag(value.liveIngest) &&
    isOptionalFlag(value.integrationLogin) &&
    isOptionalFlag(value.retained) &&
    isOptionalFlag(value.closed) &&
    isOptionalFlag(value.latestRunSucceeded) &&
    isOptionalNonNegativeInteger(value.consecutiveFailures) &&
    (value.completedRevision === undefined ||
      value.completedRevision === null ||
      isRevision(value.completedRevision))
  )
    return value as unknown as OperationRun;
  console.error('[notifications] dropped malformed run row', value);
  return null;
}

/**
 * Validates a runs snapshot. Any bad part drops the whole snapshot: read as empty, it would remove
 * every live card.
 */
export function readOperationRunsSnapshot(value: unknown): OperationRunsSnapshot | null {
  if (
    isPlainRecord(value) &&
    Array.isArray(value.runs) &&
    Number.isSafeInteger(value.revision) &&
    (value.revision as number) >= 0
  ) {
    const runs = value.runs.map(readOperationRun);
    if (runs.every((row): row is OperationRun => row !== null))
      return { runs, revision: value.revision as number };
  }
  console.error('[notifications] dropped malformed runs snapshot', value);
  return null;
}

export function createRunStoreState(): RunStoreState {
  return {
    entries: new Map(),
    buffer: new Map(),
    ended: new Map(),
    links: new Map(),
    generation: 0,
    snapshotRevision: 0,
    keptBatches: new Set()
  };
}

const cloneState = (state: RunStoreState): RunStoreState => ({
  ...state,
  entries: new Map(state.entries),
  buffer: new Map(state.buffer),
  ended: new Map(state.ended),
  links: new Map(state.links),
  keptBatches: new Set(state.keptBatches)
});

/** How a run ended, or `gone` for one removed while the server still listed it as live. */
export const endStatus = (status: OperationStatus): NotificationTerminal['status'] =>
  isTerminalNotificationStatus(status) ? status : 'gone';

const isLive = (entry: RunEntry): boolean =>
  !entry.retained && !entry.leaving && !isTerminalNotificationStatus(entry.run.status);

// A run with an owner (a prefill sign-in) is drawn only by the browser whose session started it.
const belongsToSession = (run: OperationRun, sessionId: string | null): boolean =>
  !run.ownerSessionId || run.ownerSessionId === sessionId;

const cardType = (run: OperationRun): NotificationType =>
  OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE[run.operationType];

/** The entry for an operation id, whether it is the entry's current id or one merged into it. */
function findEntry(state: RunStoreState, operationId: string): RunEntry | undefined {
  return (
    state.entries.get(operationId) ??
    [...state.entries.values()].find((entry) => entry.aliases.includes(operationId))
  );
}

function endEntry(
  next: RunStoreState,
  entry: RunEntry,
  status: NotificationTerminal['status'],
  mergedInto?: string
): void {
  next.entries.delete(entry.run.operationId);
  next.ended.set(entry.run.operationId, {
    revision: entry.run.revision,
    generation: entry.generation,
    status,
    ...(mergedInto ? { mergedInto } : {})
  });
}

const mergeDetail = (base: RunDetail, patch: RunDetail): RunDetail => ({
  ...base,
  ...patch,
  details: { ...base.details, ...patch.details }
});

function newEntry(state: RunStoreState, row: OperationRun): RunEntry {
  return {
    run: row,
    cardId: row.operationId,
    aliases: [row.operationId],
    startedAt: new Date(row.startedAt),
    generation: state.generation,
    visibility: row.visibility,
    detail: {},
    detailRevision: 0,
    recoverySeq: 0,
    lastEventSeq: 0,
    cancel: {},
    retained: false,
    leaving: false
  };
}

/**
 * One card from two entries of the same work: the older card id and start time win, so the card
 * keeps its place, and it is as visible as the successor's row says (the server raises that row
 * when the work it absorbed showed a fuller card).
 */
function mergeEntries(predecessor: RunEntry, successor: RunEntry): RunEntry {
  const older =
    predecessor.startedAt.getTime() < successor.startedAt.getTime() ||
    (predecessor.startedAt.getTime() === successor.startedAt.getTime() &&
      predecessor.cardId < successor.cardId)
      ? predecessor
      : successor;
  return {
    ...successor,
    cardId: older.cardId,
    startedAt: older.startedAt,
    aliases: [...predecessor.aliases, ...successor.aliases],
    carriedMessage: drawRun(predecessor).message,
    cancel: {
      cancelRequested: predecessor.cancel.cancelRequested || successor.cancel.cancelRequested,
      cancelSent: predecessor.cancel.cancelSent || successor.cancel.cancelSent,
      cancelPending: predecessor.cancel.cancelPending || successor.cancel.cancelPending,
      cancelling: predecessor.cancel.cancelling || successor.cancel.cancelling
    }
  };
}

/** Skip opening a new per-item singleton while the bulk card whose items produce it owns progress. */
function findBulkCardOwningType(
  type: NotificationType,
  notifications: readonly UnifiedNotification[]
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
function findBulkCardOwningOperation(
  type: NotificationType,
  operationId: string | undefined,
  notifications: readonly UnifiedNotification[]
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
 * The bulk card an item run belongs to. Any merged id counts: the queue can promote a waiting item
 * onto an operation that was already running, and the item's own id then survives only as an
 * alias of that older entry.
 */
function bulkOwner(
  entry: RunEntry,
  localCards: readonly UnifiedNotification[]
): UnifiedNotification | undefined {
  return (
    localCards.find(
      (card) =>
        card.type === 'bulk_removal' &&
        card.details?.itemOperationIds?.some((id) => entry.aliases.includes(id)) === true
    ) ??
    entry.aliases
      .map((alias) => findBulkCardOwningOperation(cardType(entry.run), alias, localCards))
      .find((card) => card !== undefined)
  );
}

/** A phase of another run (the eviction scan's detection phase): its parent's card reports it. */
function foldedUnderParent(state: RunStoreState, entry: RunEntry): boolean {
  const parent = entry.run.parentOperationId;
  return !!parent && findEntry(state, parent) !== undefined;
}

/**
 * A schedule's kept endings share one card per kind: its service key, plus the prefill schedule id.
 * Live log ingest shares one card the same way. A mapping sign-in belongs to no schedule, so its
 * failure never replaces, or is replaced by, the schedule's failure card. [55]
 */
function scheduleIdentity(run: OperationRun): string | undefined {
  if (run.liveIngest) return 'liveLogIngest';
  if (run.integrationLogin) return undefined;
  const serviceKey = SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY[cardType(run)];
  return serviceKey && (run.scheduleId ? `${serviceKey}:${run.scheduleId}` : serviceKey);
}

// The server keeps the newest kept failure of a schedule by this same order.
const endingOrder = (entry: RunEntry): number => entry.run.completedRevision ?? entry.run.revision;

/**
 * Applies one run row. See the store rules in the notification context: freshness first (a row
 * never brings back a run this browser already ended), then the merges that keep one card per
 * piece of work, then the ending rules.
 */
export function applyRun(
  state: RunStoreState,
  row: OperationRun,
  options: RunApplyOptions
): RunApplyResult {
  const unchanged: RunApplyResult = { next: state, ended: [], merged: [] };
  if (!OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE[row.operationType]) return unchanged;
  if (!belongsToSession(row, options.sessionId)) return unchanged;
  const id = row.operationId;
  const entry = state.entries.get(id);
  const record = state.ended.get(id);
  const terminal = isTerminalNotificationStatus(row.status);
  const next = cloneState(state);
  const result: RunApplyResult = { next, ended: [], merged: [] };

  // Someone closed this kept ending on some screen: it leaves everywhere.
  if (row.closed) {
    result.ended.push({
      operationId: id,
      status: endStatus(row.status),
      error: row.error ?? undefined
    });
    if (entry && !entry.leaving) next.entries.set(id, { ...entry, run: row, leaving: true });
    else if (!entry && !record)
      next.ended.set(id, {
        revision: row.revision,
        generation: next.generation,
        status: endStatus(row.status)
      });
    return result;
  }

  // A live ingest pass is never drawn and never busy, so only its kept failure is stored: a tab left
  // open through hours of downloads keeps no record per pass.
  if (row.liveIngest && !row.retained) return unchanged;
  if (entry?.leaving) return unchanged;
  // A kept ending changes only when the server republishes it with new counts.
  if (entry?.retained && !(terminal && row.revision > entry.run.revision)) return unchanged;
  const knownRevision = entry?.run.revision ?? record?.revision;
  if (knownRevision !== undefined && (entry ?? record)?.generation === state.generation) {
    if (row.revision < knownRevision) return unchanged;
    if (row.revision === knownRevision) {
      // A snapshot carries progress no push sends, because progress updates publish no row.
      if (!entry || !isLive(entry)) return unchanged;
      next.entries.set(id, {
        ...entry,
        run: { ...entry.run, percentComplete: row.percentComplete, message: row.message },
        connectionRecovering: undefined,
        hiddenHere: undefined
      });
      return result;
    }
  }
  // A card removed here while its run still looked live (a cancel answered "already finished"
  // before the terminal row arrived) comes back when the server says it kept that ending. A push
  // counts only when it is newer than every snapshot applied since, which would have held the run.
  const keptAfterRemoval =
    record?.status === 'gone' &&
    !record.mergedInto &&
    row.retained === true &&
    (!options.pushed || row.revision > state.snapshotRevision);
  if (!entry && record && terminal && !keptAfterRemoval) return unchanged;
  // That snapshot was captured after this row was stamped and did not hold the run, so the run
  // had already ended and been reaped; the row is a late copy from the publication queue.
  if (options.pushed && !entry && !record && row.revision <= state.snapshotRevision)
    return unchanged;

  // The work continues under another operation: merge this card into that operation's card.
  const successorId = row.nextOperationId;
  if (successorId && successorId !== id) {
    const successor = next.entries.get(successorId);
    const successorRecord = next.ended.get(successorId);
    if (successor || successorRecord) {
      if (entry) next.entries.delete(id);
      if (successor && entry)
        next.entries.set(successorId, { ...mergeEntries(entry, successor), hiddenHere: undefined });
      next.ended.set(id, {
        revision: row.revision,
        generation: next.generation,
        status: successorRecord ? successorRecord.status : endStatus(row.status),
        mergedInto: successorId
      });
      result.merged.push({ from: id, to: successorId });
      return result;
    }
    // Unsure where the work went: keep the card as it is until a snapshot answers.
    if (entry)
      next.entries.set(id, {
        ...entry,
        run: { ...entry.run, revision: row.revision, nextOperationId: successorId },
        generation: next.generation
      });
    const predecessors = next.links.get(successorId) ?? [];
    if (!predecessors.includes(id)) next.links.set(successorId, [...predecessors, id]);
    return result;
  }

  let current = entry;
  const absorb = (predecessorId: string): void => {
    const predecessor = next.entries.get(predecessorId);
    if (!predecessor || predecessorId === id) return;
    endEntry(next, predecessor, endStatus(predecessor.run.status), id);
    result.merged.push({ from: predecessorId, to: id });
    current = mergeEntries(predecessor, current ?? newEntry(next, row));
  };
  if (row.previousOperationId) absorb(row.previousOperationId);
  for (const predecessorId of next.links.get(id) ?? []) absorb(predecessorId);
  next.links.delete(id);

  const base = current ?? newEntry(next, row);
  let updated: RunEntry = {
    ...base,
    run: row,
    generation: next.generation,
    visibility: row.visibility,
    connectionRecovering: undefined,
    hiddenHere: undefined,
    // A parked run being cancelled has no worker to describe it, so it keeps its waiting sentence.
    ...(base.run.status === 'waiting' && row.status === 'cancelling'
      ? { carriedMessage: waitingCardMessage(base.run) }
      : {})
  };
  const buffered = next.buffer.get(id);
  if (buffered) {
    updated = {
      ...updated,
      detail: mergeDetail(updated.detail, buffered),
      detailRevision: updated.detailRevision + 1
    };
    next.buffer.delete(id);
  }
  if (!terminal) {
    next.entries.set(id, updated);
    return result;
  }

  // The ending rules. A kept ending (`retained`, decided by the server: the red and amber cards)
  // stays until someone closes it; a success or a cancel leaves at once unless Keep Notifications
  // Visible holds a card; everything else is removed. A success or cancel of a run whose start this
  // browser never saw draws nothing.
  result.ended.push({
    operationId: id,
    status: endStatus(row.status),
    error: row.error ?? undefined
  });
  const leavesOnItsOwn = row.status === 'completed' || row.status === 'cancelled';
  if (foldedUnderParent(next, updated)) {
    endEntry(next, updated, endStatus(row.status));
  } else if (bulkOwner(updated, options.localCards)) {
    // A kept item stays, folded and never drawn, so its batch card can close it on the server.
    if (row.retained) next.entries.set(id, { ...updated, retained: true });
    else endEntry(next, updated, endStatus(row.status));
  } else if (
    row.retained ||
    (leavesOnItsOwn && updated.visibility === 'card' && options.keepSuccessVisible)
  ) {
    const kept = { ...updated, retained: true };
    next.entries.set(id, kept);
    // One kept ending of each kind per schedule, replaced in this same apply so two failure cards
    // (or two skips or warnings) of one schedule are never on screen together. An ending
    // never replaces one of another kind; the server keeps the same set. The removed ending
    // resolved its waiters when it ended. [72]
    const identity = row.retained ? scheduleIdentity(row) : undefined;
    for (const other of identity ? [...next.entries.values()] : []) {
      if (other.run.operationId === id || !other.run.retained || other.run.status !== row.status)
        continue;
      if (scheduleIdentity(other.run) !== identity) continue;
      if (endingOrder(other) < endingOrder(kept)) {
        endEntry(next, other, endStatus(other.run.status));
      } else {
        endEntry(next, kept, endStatus(row.status));
        break;
      }
    }
  } else if (leavesOnItsOwn && updated.visibility !== 'hidden' && current) {
    next.entries.set(id, { ...updated, leaving: true });
  } else {
    endEntry(next, updated, endStatus(row.status));
  }
  return result;
}

/**
 * Applies a snapshot the provider has checked is the newest one of the current generation.
 * `requestSeq` is this response's request number, `issuedSeq` the newest number issued so far.
 * Returns, in `recover`, the live runs whose card still shows only its row, for detail recovery.
 */
export function applySnapshot(
  state: RunStoreState,
  snapshot: OperationRunsSnapshot,
  options: {
    keepSuccessVisible: boolean;
    localCards: readonly UnifiedNotification[];
    requestSeq: number;
    issuedSeq: number;
    sessionId: string | null;
  }
): RunApplyResult & { recover: string[] } {
  let next = cloneState(state);
  const ended: NotificationTerminal[] = [];
  const merged: RunApplyResult['merged'] = [];

  // Events were missed while disconnected, so their text is stale: show the row until detail
  // recovery or the next event refills it.
  for (const [id, entry] of next.entries)
    if (isLive(entry) && entry.generation < next.generation)
      next.entries.set(id, {
        ...entry,
        detail: {
          details: entry.detail.details,
          progressMode: entry.detail.progressMode,
          error: entry.detail.error
        },
        detailRevision: 0
      });

  for (const row of snapshot.runs) {
    const applied = applyRun(next, row, {
      keepSuccessVisible: options.keepSuccessVisible,
      localCards: options.localCards,
      pushed: false,
      sessionId: options.sessionId
    });
    next = applied.next;
    ended.push(...applied.ended);
    merged.push(...applied.merged);
  }
  const present = new Set(snapshot.runs.map((row) => row.operationId));

  // A successor this snapshot does not hold ended and was reaped. The test is the one below: only
  // a handoff this snapshot was captured after can be answered by its absence.
  for (const [successorId, predecessorIds] of next.links) {
    const answered = predecessorIds.every(
      (predecessorId) => (next.entries.get(predecessorId)?.run.revision ?? 0) <= snapshot.revision
    );
    if (!answered) continue;
    if (!next.ended.has(successorId))
      next.ended.set(successorId, { revision: 0, generation: next.generation, status: 'gone' });
    for (const predecessorId of predecessorIds) {
      const predecessor = next.entries.get(predecessorId);
      if (predecessor) endEntry(next, predecessor, 'gone', successorId);
      merged.push({ from: predecessorId, to: successorId });
    }
    next.links.delete(successorId);
  }

  for (const entry of [...next.entries.values()]) {
    if (present.has(entry.run.operationId) || entry.leaving) continue;
    if (entry.generation === next.generation && entry.run.revision > snapshot.revision) continue;
    if (!entry.retained) {
      endEntry(next, entry, 'gone');
      ended.push({ operationId: entry.run.operationId, status: 'gone' });
    } else if (entry.run.retained) {
      // Closed on another screen, and its closed row was missed: it leaves as a closed row would.
      next.entries.set(entry.run.operationId, {
        ...entry,
        run: { ...entry.run, closed: true },
        leaving: true
      });
    }
  }

  next.snapshotRevision = Math.max(next.snapshotRevision, snapshot.revision);

  // A record may go only once no row for its id can still be admitted: a request that STARTED
  // after the first absence was applied read its revision after the run was reaped.
  for (const [id, record] of next.ended) {
    if (present.has(id)) {
      if (record.absentSeq !== undefined) next.ended.set(id, { ...record, absentSeq: undefined });
    } else if (record.absentSeq === undefined) {
      next.ended.set(id, { ...record, absentSeq: options.issuedSeq });
    } else if (options.requestSeq > record.absentSeq) {
      next.ended.delete(id);
    }
  }
  for (const id of [...next.buffer.keys()]) if (!present.has(id)) next.buffer.delete(id);

  // A producer can move the tracker's progress before its broadcast throttle, so the row's percent
  // can be newer than the last event's; it is, unless an event arrived after this request started.
  for (const [id, entry] of next.entries)
    if (isLive(entry) && present.has(id) && entry.lastEventSeq < options.requestSeq) {
      const detail = { ...entry.detail };
      delete detail.progress;
      delete detail.progressAriaValueText;
      next.entries.set(id, { ...entry, detail });
    }

  const recover = [...next.entries.values()]
    .filter((entry) => isLive(entry) && entry.detailRevision === 0)
    .map((entry) => entry.run.operationId);
  return { next, ended, merged, recover };
}

/** A new SignalR connection: a restarted server numbers its rows from 1 again. */
export function nextGeneration(state: RunStoreState): RunStoreState {
  const next = cloneState(state);
  next.generation += 1;
  next.snapshotRevision = 0;
  for (const [id, record] of next.ended)
    if (record.absentSeq !== undefined) next.ended.set(id, { ...record, absentSeq: undefined });
  return next;
}

/**
 * A new signed-in session, or none after a sign-out: every run another session owns leaves now,
 * kept or not, and a run list requested under the earlier session is refused by its generation.
 */
export function changeSession(state: RunStoreState, sessionId: string | null): RunStoreState {
  const next = cloneState(state);
  for (const entry of [...next.entries.values()])
    if (!belongsToSession(entry.run, sessionId)) endEntry(next, entry, 'gone');
  return nextGeneration(next);
}

/**
 * Applies a detail patch to a run. `build` receives the card currently drawn for the run. A
 * `completion` patch (the per-type completion event) is the only one a finished run accepts, and
 * it changes the text alone, never the run's status or how long its card stays. A `recovery` patch
 * applies only while nothing newer reached the entry since the recovery request started: same
 * generation, same event count, and no newer recovery applied.
 */
export function applyDetail(
  state: RunStoreState,
  operationId: string,
  build: (existing: UnifiedNotification | undefined) => RunDetail | null,
  source: 'event' | 'completion' | 'recovery',
  fence: { requestSeq: number; generation?: number; detailRevision?: number }
): RunStoreState {
  const entry = findEntry(state, operationId);
  if (!entry) {
    if (source === 'recovery') return state;
    const patch = build(undefined);
    if (!patch) return state;
    const next = cloneState(state);
    const buffered = mergeDetail(next.buffer.get(operationId) ?? {}, patch);
    next.buffer.delete(operationId);
    next.buffer.set(operationId, buffered);
    // The buffer only bridges the moment between an event and its run's first row.
    if (next.buffer.size > 64) {
      const [oldest] = next.buffer.keys();
      next.buffer.delete(oldest);
    }
    return next;
  }
  if (!isLive(entry) && source !== 'completion') return state;
  if (
    source === 'recovery' &&
    (fence.generation !== state.generation ||
      fence.detailRevision !== entry.detailRevision ||
      fence.requestSeq <= entry.recoverySeq)
  )
    return state;
  const patch = build(drawRun(entry));
  if (!patch) return state;

  const next = cloneState(state);
  const key = entry.run.operationId;
  const updated: RunEntry = {
    ...entry,
    connectionRecovering: undefined,
    // A completion does not say the run is still running; its ending row brings a closed card back.
    hiddenHere: source === 'completion' ? entry.hiddenHere : undefined
  };
  if (cardType(entry.run) === 'scheduled_prefill') {
    const stream = entry.stream;
    const series = patch.details?.eventEpoch;
    const sequence = patch.details?.eventSequence;
    const version =
      series && typeof sequence === 'number' && Number.isSafeInteger(sequence) && sequence > 0
        ? { series, sequence }
        : undefined;
    const daemon = patch.details?.daemonInstanceId;
    if (stream && daemon && stream.daemonInstanceId && daemon !== stream.daemonInstanceId)
      return state;
    if (source === 'recovery') {
      if (stream && !version) return state;
      if (stream && version && version.series === stream.series) {
        if (version.sequence < stream.sequence) return state;
        if (version.sequence === stream.sequence) {
          next.entries.set(key, { ...updated, recoverySeq: fence.requestSeq });
          return next;
        }
      }
      if (version) {
        // The run-status response names the current series: adopt it and retire the old one.
        const retired = new Set(stream?.retired);
        if (stream && stream.series !== version.series) retired.add(stream.series);
        updated.stream = {
          ...stream,
          ...version,
          daemonInstanceId: daemon ?? stream?.daemonInstanceId,
          retired
        };
      }
    } else {
      if (stream && !version) return state;
      if (stream && version && version.series !== stream.series) {
        if (stream.retired.has(version.series)) return state;
        const held = stream.pending;
        if (
          held &&
          held.series === version.series &&
          (held.source === 'completion' ||
            (source !== 'completion' && version.sequence <= held.sequence))
        )
          return state;
        next.entries.set(key, {
          ...entry,
          stream: { ...stream, pending: { ...version, detail: patch, source } }
        });
        return next;
      }
      if (stream && version && version.sequence <= stream.sequence) return state;
      if (version)
        updated.stream = {
          ...stream,
          ...version,
          daemonInstanceId: daemon ?? stream?.daemonInstanceId,
          retired: stream?.retired ?? new Set()
        };
    }
  }

  updated.detail = mergeDetail(updated.detail, patch);
  if (source === 'recovery') {
    updated.recoverySeq = fence.requestSeq;
    const adopted = updated.stream;
    const held = adopted?.pending;
    if (adopted && held && held.series === adopted.series) {
      // The held patch belongs to the adopted series; it applies only when it is newer.
      const newer = held.sequence > adopted.sequence;
      updated.stream = {
        series: adopted.series,
        sequence: newer ? held.sequence : adopted.sequence,
        daemonInstanceId: adopted.daemonInstanceId,
        retired: adopted.retired
      };
      if (newer) {
        updated.detail = mergeDetail(updated.detail, held.detail);
        updated.detailRevision += 1;
      }
    }
  } else {
    updated.detailRevision += 1;
    updated.lastEventSeq = fence.requestSeq;
  }
  next.entries.set(key, updated);
  return next;
}

/** Ends runs by operation id (a card closed by hand, a run whose fade finished). */
export function removeRuns(state: RunStoreState, operationIds: readonly string[]): RunStoreState {
  const next = cloneState(state);
  for (const id of operationIds) {
    const entry = findEntry(next, id);
    if (entry) endEntry(next, entry, endStatus(entry.run.status));
  }
  return next;
}

/** Keep Notifications Visible was turned off: the successes and cancels it held leave on their own. */
export function releaseKeptSuccess(state: RunStoreState): RunStoreState {
  const next = cloneState(state);
  for (const [id, entry] of next.entries)
    if (entry.retained && !entry.run.retained)
      next.entries.set(id, { ...entry, retained: false, leaving: true });
  return next;
}

/** The connection dropped: a live prefill card says it is reconnecting until detail arrives. */
export function markConnectionRecovering(state: RunStoreState): RunStoreState {
  const next = cloneState(state);
  for (const [id, entry] of next.entries)
    if (isLive(entry) && cardType(entry.run) === 'scheduled_prefill')
      next.entries.set(id, { ...entry, connectionRecovering: true });
  return next;
}

/** The only part of a run card the browser writes: the cancel intent of its X button. */
export function setRunCancel(
  state: RunStoreState,
  cardId: string,
  cancel: RunEntry['cancel']
): RunStoreState {
  const entry = [...state.entries.values()].find((candidate) => candidate.cardId === cardId);
  if (!entry) return state;
  const next = cloneState(state);
  next.entries.set(entry.run.operationId, { ...entry, cancel: { ...entry.cancel, ...cancel } });
  return next;
}

/**
 * Hides a live run card on this screen while the server is unreachable. The entry stays, so the
 * run keeps its page busy and its waiters, and the next row for it draws the card again.
 */
export function hideRun(state: RunStoreState, cardId: string): RunStoreState {
  const entry = [...state.entries.values()].find((candidate) => candidate.cardId === cardId);
  // A row may have ended the run after the card was drawn; its ending must stay on screen.
  if (!entry || !isLive(entry)) return state;
  const next = cloneState(state);
  // A cancel still in flight settles against the drawn cards, which no longer hold this one, so
  // its pending flags would outlive the hide; the server's next row says whether it landed.
  next.entries.set(entry.run.operationId, { ...entry, hiddenHere: true, cancel: {} });
  return next;
}

/**
 * A failed batch card leaves once the last kept failure folded under it was closed somewhere else,
 * checked whenever a kept run leaves and whenever the batch ends. A batch that never owned a kept
 * run, or one with a failure that never reached the server, stays until closed here.
 */
export function settleBulkCards(
  state: RunStoreState,
  localCards: readonly UnifiedNotification[]
): { next: RunStoreState; release: string[] } {
  const holding = new Set<string>();
  for (const entry of state.entries.values()) {
    // A kept run that is fading out was already closed; it holds nothing.
    const owner = entry.run.retained && !entry.leaving ? bulkOwner(entry, localCards) : undefined;
    if (owner) holding.add(owner.id);
  }
  const added = [...holding].filter((id) => !state.keptBatches.has(id));
  const next =
    added.length === 0
      ? state
      : { ...state, keptBatches: new Set([...state.keptBatches, ...added]) };
  const release = localCards
    .filter(
      (card) =>
        card.type === 'bulk_removal' &&
        card.status === 'failed' &&
        next.keptBatches.has(card.id) &&
        !holding.has(card.id) &&
        card.details?.failedWithoutRun !== true
    )
    .map((card) => card.id);
  return { next, release };
}

/** Where a waiter on `operationId` stands: the run's current id and, once it ended, how. */
export function locateRun(
  state: RunStoreState,
  operationId: string
): { operationId: string; known: boolean; terminal?: NotificationTerminal } {
  let id = operationId;
  for (;;) {
    const entry = findEntry(state, id);
    if (entry) {
      const run = entry.run;
      return {
        operationId: run.operationId,
        known: true,
        terminal: isTerminalNotificationStatus(run.status)
          ? {
              operationId: run.operationId,
              status: endStatus(run.status),
              error: run.error ?? undefined
            }
          : undefined
      };
    }
    const record = state.ended.get(id);
    if (!record) return { operationId: id, known: false };
    if (!record.mergedInto)
      return { operationId: id, known: true, terminal: { operationId: id, status: record.status } };
    id = record.mergedInto;
  }
}

function drawRun(entry: RunEntry): UnifiedNotification {
  const { run, detail } = entry;
  const live = !isTerminalNotificationStatus(run.status);
  // A sign-in waits on a person, so its card names the platform and shows no percentage.
  const signIn = live && cardType(run) === 'prefill_login';
  // A sign-in card names its platform the way the sign-in dialogs do, live and failed alike; the
  // server's reason stays the error line.
  const failedSignIn = run.status === 'failed' && cardType(run) === 'prefill_login';
  const platform =
    signIn || failedSignIn
      ? i18n.t(
          `prefill.persistent.services.${SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[run.serviceId ?? '']}`
        )
      : undefined;
  const status =
    live && (entry.cancel.cancelRequested || entry.cancel.cancelling) ? 'cancelling' : run.status;
  const kept = run.retained === true && !live;
  const warning = run.status === 'completed' && run.warning ? run.warning : undefined;
  const line = warning
    ? (detail.detailMessage ?? detectionErrorDetail({ context: { detectionError: warning } }))
    : detail.detailMessage;
  const failures = run.consecutiveFailures ?? 0;
  const lines = [
    kept && failures > 1
      ? i18n.t('common.notifications.failedTimesInRow', {
          count: failures,
          formattedCount: formatCount(failures)
        })
      : undefined,
    kept && run.latestRunSucceeded ? i18n.t('common.notifications.latestRunSucceeded') : undefined,
    line
  ].filter((part): part is string => part !== undefined);
  const controlOnly = live && entry.visibility === 'background' ? true : undefined;
  return {
    id: entry.cardId,
    type: cardType(run),
    status,
    controlOnly,
    message:
      run.status === 'waiting'
        ? // A background row prints the run's name beside its message, so it names only the blocker.
          waitingCardMessage(controlOnly ? { blockedByName: run.blockedByName } : run)
        : signIn
          ? i18n.t('prefill.auth.waitingForSignIn', { service: platform })
          : failedSignIn
            ? i18n.t('common.errors.signInFailed', { platform })
            : (detail.message ??
              (run.status === 'skipped'
                ? // A skip's row message is the reason the server kept, which the live card prints as is.
                  translateStageKeyMessage(run.message, undefined, GENERIC_SKIPPED_I18N_KEY)
                : isTerminalNotificationStatus(run.status)
                  ? // An ended run no event described says how it ended, never "in progress". [56]
                    translateRecoveryStage(
                      run.message,
                      undefined,
                      {
                        completed: GENERIC_COMPLETION_I18N_KEY,
                        failed: GENERIC_FAILURE_I18N_KEY,
                        cancelled: GENERIC_CANCELLED_I18N_KEY
                      }[run.status]
                    )
                  : (entry.carriedMessage ??
                    translateRecoveryStage(run.message, undefined, 'signalr.generic.unknown')))),
    detailMessage: lines.length > 0 ? lines.join(' ') : undefined,
    progress:
      'progress' in detail
        ? detail.progress
        : run.status === 'running' && detail.progressMode !== 'indeterminate' && !signIn
          ? run.percentComplete
          : undefined,
    progressMode: detail.progressMode,
    progressAriaValueText: detail.progressAriaValueText,
    startedAt: entry.startedAt,
    error: run.status === 'failed' ? (detail.error ?? run.error ?? undefined) : undefined,
    details: {
      ...(run.serviceId ? { service: run.serviceId } : {}),
      ...(run.scheduleId ? { scheduleId: run.scheduleId } : {}),
      ...detail.details,
      operationId: run.operationId,
      operationIds: entry.aliases,
      parentOperationId: run.parentOperationId ?? undefined,
      ...entry.cancel,
      cancelled: status === 'cancelled' || undefined,
      connectionRecovering: entry.connectionRecovering,
      ...(kept ? { closeOperationIds: [run.operationId] } : {}),
      ...(warning ? { notificationType: 'warning' as const } : {})
    }
  };
}

/**
 * Every card to draw: run cards first, then the browser's own cards. A run folded under a parent
 * or a bulk card draws nothing of its own; a waiting item turns its bulk card purple, and the bulk
 * card lists the kept runs folded under it so its dismiss can close them on the server.
 */
export function deriveNotifications(
  state: RunStoreState,
  localCards: readonly UnifiedNotification[]
): UnifiedNotification[] {
  const cards: UnifiedNotification[] = [];
  const waitingItem = new Map<string, OperationRun>();
  const keptItems = new Map<string, string[]>();
  for (const entry of state.entries.values()) {
    if (foldedUnderParent(state, entry)) continue;
    const owner = bulkOwner(entry, localCards);
    if (owner) {
      if (entry.run.status === 'waiting') waitingItem.set(owner.id, entry.run);
      if (entry.run.retained && !entry.leaving)
        keptItems.set(owner.id, [...(keptItems.get(owner.id) ?? []), entry.run.operationId]);
      continue;
    }
    // A kept card always shows; a fading one only if it was a full card; a live one unless Hidden.
    const drawn = entry.retained
      ? true
      : entry.leaving
        ? entry.visibility === 'card'
        : entry.visibility !== 'hidden';
    if (drawn && !entry.hiddenHere) cards.push(drawRun(entry));
  }
  for (const card of localCards) {
    const waiting = isTerminalNotificationStatus(card.status)
      ? undefined
      : waitingItem.get(card.id);
    const kept = keptItems.get(card.id);
    cards.push(
      !waiting && !kept
        ? card
        : {
            ...card,
            ...(waiting
              ? { status: 'waiting' as const, message: waitingCardMessage(waiting) }
              : {}),
            details: { ...card.details, ...(kept ? { closeOperationIds: kept } : {}) }
          }
    );
  }
  return cards;
}

/**
 * Every live run, Hidden ones included, as a card object: what keeps a page's own buttons busy
 * until the run ends. A phase folded under a parent run is left out; bulk items stay in. A live
 * ingest pass and another session's sign-in are never stored, so they are never here.
 */
export function deriveRuns(state: RunStoreState): UnifiedNotification[] {
  return [...state.entries.values()]
    .filter(
      (entry) => !isTerminalNotificationStatus(entry.run.status) && !foldedUnderParent(state, entry)
    )
    .map(drawRun);
}
