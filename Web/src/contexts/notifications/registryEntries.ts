/**
 * Shared shapes for the entries in {@link NOTIFICATION_REGISTRY}.
 *
 * Three builders live here, one per lifecycle family:
 *   - {@link buildStandardOperationEntry} - a cancellable server operation with a
 *     Started -> Progress -> Complete event triple and per-type messages.
 *   - {@link buildMappingOperationEntry} - the five scheduled mapping services,
 *     combining that standard lifecycle with shared tracker recovery.
 *   - {@link buildScheduledRunEntry} - a pipeline-less maintenance service whose
 *     whole lifecycle is derived from an event prefix and an i18n base.
 *
 * All three derive the SignalR event names from one `eventPrefix`, so the names
 * cannot drift apart from each other and a new type cannot register two events
 * for one pipeline and a third for another. The names are therefore NOT literals
 * in the registry: a text search for `DataImportStarted` will not find the
 * subscription, but the event is subscribed exactly like any other.
 *
 * The getter factories below cover the readings that repeat across most entries
 * (stage-keyed message, capped progress, operation-id details, the silent-run
 * display gate). None of them are mandatory - an entry that needs a different
 * reading passes its own function and the builder uses that instead.
 */

import i18n from '@/i18n';
import { translateRecoveryStage } from '@utils/stageKeyMessage';
import { ACTIVE_PROGRESS_PERCENT_CAP, GENERIC_FAILURE_I18N_KEY } from './constants';
import type {
  NotificationProgressMode,
  NotificationRegistryEntry,
  NotificationType,
  RecoveryConfig,
  RegistryCompleteConfig,
  RegistryProgressConfig,
  RegistryStartedConfig,
  RemoveNotification,
  SimpleRecoveryConfig,
  StageContext,
  UnifiedNotification
} from './types';
import type {
  ScheduledRunStartedEvent,
  ScheduledRunProgressEvent,
  ScheduledRunCompleteEvent,
  MappingStartedEvent as MappingStartedContract,
  MappingProgressEvent as MappingProgressContract,
  MappingCompleteEvent as MappingCompleteContract
} from '../SignalRContext/types';

// ============================================================================
// Event field Models
// ============================================================================
// Each interface names one field group a getter reads. They are deliberately
// narrower than the concrete SignalR event interfaces so one getter can serve
// every event carrying that group, while the entries keep their concrete event
// types on the functions they write themselves.

/** Carries the server operation id that the card's cancel wiring needs. */
interface OperationIdEvent {
  operationId?: string;
}

/**
 * Carries the run-stable display flag. Lifecycle events are ALWAYS emitted; a
 * silent run streams them and renders no card, so the gate is `!== false` - an
 * event that omits the flag is visible.
 */
interface SilentRunEvent {
  showNotification?: boolean;
}

/** Carries an i18n stage key and its interpolation context. */
interface StageKeyEvent {
  stageKey?: string;
  context?: StageContext | null;
}

/** Carries a completion percentage. */
interface PercentCompleteEvent {
  percentComplete: number;
}

/** A terminal event that may report a server-formatted error instead of a stage key. */
interface TerminalStageKeyEvent extends StageKeyEvent {
  error?: string | null;
}

/** Carries an operation status string from the shared backend `OperationStatus`. */
interface OperationStatusEvent {
  status?: string;
}

// ============================================================================
// Shared getters
// ============================================================================

/**
 * Standard three-status pattern: 'completed' -> 'completed',
 * 'failed'|'cancelled' -> 'failed', everything else -> undefined.
 *
 * The cancelled arm never fires in practice: every server-side cancellation is reported on a
 * Complete payload, never on Progress, and the completion handler reads the cancel flag itself.
 * It stays because a returned 'cancelled' would otherwise fall through to undefined here, which
 * the progress handler reads as "still running" - a stopped card frozen mid-run is worse than a
 * stopped card shown as failed. A cancelled run that reaches a user reads grey, not red.
 */
function standardGetStatus(event: OperationStatusEvent): string | undefined {
  if (event.status === 'completed') return 'completed';
  if (event.status === 'failed' || event.status === 'cancelled') return 'failed';
  return undefined;
}

/** Display gate for services whose runs can be configured silent. */
function visibleWhenNotSilent(event: SilentRunEvent): boolean {
  return event.showNotification !== false;
}

/**
 * The details every standard operation card needs. The cancel button and the
 * deferred-cancel watchdog both key off `details.operationId`, so a card without
 * it renders a cancel affordance that cannot cancel anything.
 */
export function operationIdDetails(event: OperationIdEvent): UnifiedNotification['details'] {
  return { operationId: event.operationId };
}

/** Translates the event's stage key, falling back to a fixed key when it is absent. */
export function stageKeyMessage<TEvent extends StageKeyEvent = StageKeyEvent>(
  fallbackKey: string
): (event: TEvent) => string {
  return (event) => i18n.t(event.stageKey ?? fallbackKey, event.context ?? {});
}

/**
 * Terminal message precedence: a server-formatted error wins, then the event's own
 * stage key, then a fixed fallback key.
 */
export function errorOrStageKeyMessage<
  TEvent extends TerminalStageKeyEvent = TerminalStageKeyEvent
>(fallbackKey: string): (event: TEvent) => string {
  return (event) =>
    event.error ??
    (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
    i18n.t(fallbackKey);
}

/** Holds a running card below 100% so only the terminal event can complete the bar. */
export function cappedProgress(event: PercentCompleteEvent): number {
  return Math.min(ACTIVE_PROGRESS_PERCENT_CAP, event.percentComplete);
}

// ============================================================================
// Standard operation entries
// ============================================================================

/**
 * The three SignalR payloads one operation's lifecycle produces. Naming them at the
 * call site is how an entry declares its wire contract: every getter below is then
 * checked against the real event shape instead of against `any`.
 */
interface StandardOperationEntryOptions<TStarted, TProgress, TComplete> {
  type: NotificationType;
  id: string;
  storageKey: string;
  /** SignalR event-name prefix, e.g. 'DataImport' -> DataImportStarted/Progress/Complete. */
  eventPrefix: string;
  /** Terminal event name, for the one pipeline that named its event `...Completed`. */
  completeEvent?: string;
  cancelTooltipKey: string;
  /** Set for a pipeline whose first operationId can arrive on a progress tick. */
  allowsDeferredCancel?: boolean;
  recovery: RecoveryConfig;
  /**
   * Applies the silent-run display gate to all three lifecycle phases. Set it for
   * services whose runs can be configured to render no card. A phase that passes
   * its own `shouldDisplay` keeps it.
   */
  silentRunGate?: boolean;
  started: RegistryStartedConfig<TStarted>;
  /** `getStatus` falls back to the three-status pattern when omitted. */
  progress: Omit<RegistryProgressConfig<TProgress>, 'getStatus'> &
    Partial<Pick<RegistryProgressConfig<TProgress>, 'getStatus'>>;
  complete?: RegistryCompleteConfig<TComplete>;
  onComplete?: (removeNotification: RemoveNotification) => void;
}

/**
 * Builds the entry for a cancellable server operation: a serverOp cancel, the event
 * triple derived from `eventPrefix`, and the operation-id details every card in this
 * family carries.
 */
export function buildStandardOperationEntry<TStarted, TProgress, TComplete>(
  options: StandardOperationEntryOptions<TStarted, TProgress, TComplete>
): NotificationRegistryEntry {
  const {
    type,
    id,
    storageKey,
    eventPrefix,
    completeEvent,
    cancelTooltipKey,
    allowsDeferredCancel,
    recovery,
    silentRunGate,
    started,
    progress,
    complete,
    onComplete
  } = options;

  // The locals drop the concrete event type, exactly as `NotificationRegistryEntry`
  // does: the checking that matters happened on `options`, where the entry declared
  // its wire contract. From here on the configs are handler plumbing.
  const startedConfig: RegistryStartedConfig = {
    ...started,
    getDetails: started.getDetails ?? operationIdDetails
  };
  const progressConfig: RegistryProgressConfig = {
    ...progress,
    getStatus: progress.getStatus ?? standardGetStatus,
    getDetails: progress.getDetails ?? operationIdDetails
  };
  const completeConfig: RegistryCompleteConfig | undefined = complete ? { ...complete } : undefined;

  // Only set the gate where one applies, so a non-silent entry keeps exactly the
  // config shape it had when it was written out by hand.
  if (silentRunGate) {
    startedConfig.shouldDisplay ??= visibleWhenNotSilent;
    progressConfig.shouldDisplay ??= visibleWhenNotSilent;
    if (completeConfig) completeConfig.shouldDisplay ??= visibleWhenNotSilent;
  }

  return {
    type,
    id,
    storageKey,
    cancelKind: 'serverOp',
    cancelTooltipKey,
    allowsDeferredCancel,
    recovery,
    events: {
      started: `${eventPrefix}Started`,
      progress: `${eventPrefix}Progress`,
      complete: completeEvent ?? `${eventPrefix}Complete`
    },
    started: startedConfig,
    progress: progressConfig,
    ...(completeConfig ? { complete: completeConfig } : {}),
    ...(onComplete ? { onComplete } : {})
  };
}

// ============================================================================
// Mapping operation entries
// ============================================================================

interface MappingRunStatusResponse {
  isRunning: boolean;
  operationId?: string | null;
  percentComplete: number;
  stageKey?: string | null;
  context?: StageContext | null;
  showNotification: boolean;
}

interface MappingOperationEntryOptions {
  type: NotificationType;
  id: string;
  storageKey: string;
  serviceKey: string;
  eventPrefix: string;
  /** Terminal event name, for a pipeline that named its event `...Completed`. */
  completeEvent?: string;
  i18nBase: string;
  cancelTooltipKey: string;
  defaultMessage: string;
  staleMessageKey: string;
  recoveryCases: readonly { stageKey: string; context: StageContext }[];
}

/**
 * Builds one of the five mapping cards. All mapping services share the operation
 * tracker endpoint, server-operation cancellation, silent-run display gate, and
 * canonical lifecycle payload; only identity and translations vary by platform.
 */
export function buildMappingOperationEntry<
  TStarted extends MappingStartedContract,
  TProgress extends MappingProgressContract,
  TComplete extends MappingCompleteContract
>(options: MappingOperationEntryOptions): NotificationRegistryEntry {
  const {
    type,
    id,
    storageKey,
    serviceKey,
    eventPrefix,
    completeEvent,
    i18nBase,
    cancelTooltipKey,
    defaultMessage,
    staleMessageKey,
    recoveryCases
  } = options;

  return buildStandardOperationEntry<TStarted, TProgress, TComplete>({
    type,
    id,
    storageKey,
    eventPrefix,
    completeEvent,
    cancelTooltipKey,
    silentRunGate: true,
    recovery: {
      kind: 'simple',
      translationValidation: { kind: 'stageKey', cases: recoveryCases },
      apiEndpoint: `/api/system/schedules/${serviceKey}/run-status`,
      isProcessing: (data: MappingRunStatusResponse) => data.isRunning,
      shouldSkip: (data: MappingRunStatusResponse) =>
        data.isRunning && data.showNotification === false,
      createNotification: (data: MappingRunStatusResponse) => ({
        message: translateRecoveryStage(
          data.stageKey,
          data.context ?? undefined,
          `${i18nBase}.starting`
        ),
        progress: Math.min(ACTIVE_PROGRESS_PERCENT_CAP, data.percentComplete),
        details: { operationId: data.operationId ?? undefined }
      }),
      staleMessageKey
    } satisfies SimpleRecoveryConfig<MappingRunStatusResponse>,
    started: {
      defaultMessage,
      getMessage: stageKeyMessage<TStarted>(`${i18nBase}.starting`),
      replaceExisting: true,
      // A mapping run registers before its sign-in, so the card can sit here for a quarter of an
      // hour while a person reads a device code off their phone. The started handler's progress: 0
      // would draw a bar frozen at nothing for all of it; sweep instead until real progress lands.
      progressMode: 'indeterminate'
    },
    progress: {
      getMessage: stageKeyMessage<TProgress>(`${i18nBase}.starting`),
      getProgress: cappedProgress,
      // Ends the sweep above: from the first progress event the percentage is real.
      getProgressMode: (): NotificationProgressMode => 'determinate',
      getCompletedMessage: stageKeyMessage<TProgress>(`${i18nBase}.completed`),
      getErrorMessage: errorOrStageKeyMessage<TProgress>(`${i18nBase}.failed`),
      supportFastCompletion: true
    },
    complete: {
      getSuccessMessage: stageKeyMessage<TComplete>(`${i18nBase}.completed`),
      getFailureMessage: errorOrStageKeyMessage<TComplete>(`${i18nBase}.failed`),
      getCancelledMessage: stageKeyMessage<TComplete>(`${i18nBase}.cancelled`),
      getSuccessDetails: operationIdDetails,
      getCancelledDetails: operationIdDetails
    }
  });
}

// ============================================================================
// Scheduled service run entries (pipeline-less maintenance services)
// ============================================================================
// Each of these services runs on a schedule (or via Run Now) and emits a
// per-service lifecycle event triple carrying a run-stable `showNotification`
// flag. Lifecycle events are ALWAYS emitted; the frontend honours the flag
// through shouldDisplay gates, so a silent run streams events but renders no
// card. Card identity is per service (per-type singleton id) because several of
// these can run concurrently.

/** GET /api/system/schedules/{serviceKey}/run-status - ScheduleRunStatus */
interface ScheduledRunStatusResponse {
  isRunning: boolean;
  operationId?: string | null;
  percentComplete: number;
  stageKey?: string;
  context?: StageContext | null;
  showNotification: boolean;
}

interface ScheduledRunEntryOptions {
  type: NotificationType;
  id: string;
  storageKey: string;
  /** URL segment + backend serviceKey used for the run-status recovery endpoint. */
  serviceKey: string;
  /** SignalR event-name prefix, e.g. 'LogRotation' -> LogRotationStarted/Progress/Complete. */
  eventPrefix: string;
  /** i18n base, e.g. 'signalr.scheduledRun.logRotation'. */
  i18nBase: string;
  /** Countable services interpolate {{processed}}/{{total}} into their `.running` string. */
  countable: boolean;
  /** Plain fallback shown before the first stage-keyed message arrives. */
  defaultMessage: string;
  /** Key for the terminal fallback shown when a run card outlived its terminal event. */
  staleMessageKey: string;
}

export function buildScheduledRunEntry(
  options: ScheduledRunEntryOptions
): NotificationRegistryEntry {
  const {
    type,
    id,
    storageKey,
    serviceKey,
    eventPrefix,
    i18nBase,
    countable,
    defaultMessage,
    staleMessageKey
  } = options;
  return {
    type,
    id,
    storageKey,
    cancelKind: 'none',
    recovery: {
      kind: 'simple',
      translationValidation: {
        kind: 'stageKey',
        cases: [
          { stageKey: `${i18nBase}.starting`, context: {} },
          {
            stageKey: `${i18nBase}.running`,
            context: countable ? { processed: 10, total: 100 } : {}
          },
          { stageKey: `${i18nBase}.complete`, context: {} }
        ]
      },
      apiEndpoint: `/api/system/schedules/${serviceKey}/run-status`,
      isProcessing: (data: ScheduledRunStatusResponse) => data.isRunning,
      // A silent run must not resurrect a card when the page reloads mid-run. Only skip an ACTIVE
      // silent run: an idle service reports showNotification=true so a persisted running card is
      // stale-completed on reconnect, never deleted, after a missed terminal (mirrors scheduledPrefill).
      shouldSkip: (data: ScheduledRunStatusResponse) =>
        data.isRunning && data.showNotification === false,
      createNotification: (data: ScheduledRunStatusResponse) => ({
        message: translateRecoveryStage(
          data.stageKey,
          data.context ?? undefined,
          `${i18nBase}.starting`
        ),
        progress: data.percentComplete,
        details: { operationId: data.operationId ?? undefined }
      }),
      staleMessageKey
    } satisfies SimpleRecoveryConfig<ScheduledRunStatusResponse>,
    events: {
      started: `${eventPrefix}Started`,
      progress: `${eventPrefix}Progress`,
      complete: `${eventPrefix}Complete`
    },
    started: {
      shouldDisplay: visibleWhenNotSilent,
      defaultMessage,
      getMessage: stageKeyMessage<ScheduledRunStartedEvent>(`${i18nBase}.starting`),
      getDetails: operationIdDetails
    } satisfies RegistryStartedConfig<ScheduledRunStartedEvent>,
    progress: {
      shouldDisplay: visibleWhenNotSilent,
      getMessage: stageKeyMessage<ScheduledRunProgressEvent>(`${i18nBase}.running`),
      getProgress: cappedProgress,
      getStatus: standardGetStatus,
      getCompletedMessage: stageKeyMessage<ScheduledRunProgressEvent>(`${i18nBase}.complete`),
      getErrorMessage: stageKeyMessage<ScheduledRunProgressEvent>(GENERIC_FAILURE_I18N_KEY),
      getDetails: operationIdDetails
    } satisfies RegistryProgressConfig<ScheduledRunProgressEvent>,
    complete: {
      shouldDisplay: visibleWhenNotSilent,
      getSuccessMessage: stageKeyMessage<ScheduledRunCompleteEvent>(`${i18nBase}.complete`),
      getFailureMessage: errorOrStageKeyMessage<ScheduledRunCompleteEvent>(GENERIC_FAILURE_I18N_KEY)
    } satisfies RegistryCompleteConfig<ScheduledRunCompleteEvent>
  };
}
