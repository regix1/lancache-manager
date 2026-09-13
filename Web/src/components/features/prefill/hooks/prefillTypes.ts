export interface PrefillProgress {
  operationId?: string;
  daemonInstanceId?: string;
  sequence?: number;
  skippedApps?: number;
  cancelledApps?: number;
  stageKey?: string;
  errorMessage?: string;
  totalBytesTransferred?: number;
  reason?: string | null;
  updatedAt?: string;
  state: string;
  message?: string;
  currentAppId: string;
  currentAppName?: string;
  percentComplete: number;
  bytesDownloaded: number;
  totalBytes: number;
  bytesPerSecond: number;
  elapsedSeconds: number;
  /**
   * Total number of apps in this prefill job (when known). Used to render the two-tier
   * "Game X of N" overall bar. Seeded from `expectedAppCount` / the daemon's `totalApps`.
   */
  expectedAppCount?: number;
  /** Total apps in the job per the daemon (camelCase via SignalR). */
  totalApps?: number;
  /** Count of apps downloaded so far in the job (camelCase via SignalR). */
  updatedApps?: number;
  /** Count of apps already up-to-date in the job (camelCase via SignalR). */
  alreadyUpToDate?: number;
  /** Count of apps that failed in the job (camelCase via SignalR). */
  failedApps?: number;
}

export interface PrefillRunItem {
  appId: string;
  name?: string | null;
  state: string;
  result?: string | null;
  reason?: string | null;
  sequence: number;
  bytesTransferred: number;
  totalBytes?: number | null;
}

export interface PrefillRun {
  runId: string;
  sessionId: string;
  daemonInstanceId: string;
  scheduleId?: string | null;
  scheduleName?: string | null;
  notificationMode?: string | null;
  parentOperationId?: string | null;
  options: {
    appIds?: string[] | null;
    selection: string;
    force: boolean;
    operatingSystems: string[];
    maxConcurrency: number;
    topCount?: number | null;
  };
  snapshot: {
    operationId: string;
    daemonInstanceId: string;
    sequence: number;
    startedAt: string;
    updatedAt: string;
    state: string;
    reason?: string | null;
    selectionResolved: boolean;
    currentItem?: PrefillRunItem | null;
    totalApps: number;
    completedApps: number;
    cachedApps: number;
    failedApps: number;
    cancelledApps: number;
    skippedApps: number;
    bytesTransferred: number;
  };
  progress?: PrefillProgress | null;
  recovering: boolean;
  cancelRequested: boolean;
  historyIncomplete: boolean;
  completedAtUtc?: string | null;
}

export function isPrefillRunActive(run: PrefillRun): boolean {
  return !['completed', 'failed', 'cancelled'].includes(run.snapshot.state);
}

export function prefillRunKey(run: PrefillRun): string {
  return `${run.sessionId}:${run.daemonInstanceId}:${run.runId}`;
}

export interface PrefillCompletion {
  key: string;
  completedAt: number;
  dismissed: boolean;
}

export function retainPrefillCompletions(
  entries: PrefillCompletion[],
  now = Date.now()
): PrefillCompletion[] {
  if (!Array.isArray(entries)) return [];
  return entries
    .filter(
      (entry) =>
        entry &&
        typeof entry.key === 'string' &&
        Number.isFinite(entry.completedAt) &&
        entry.completedAt > now - 86400000 &&
        entry.completedAt <= now
    )
    .sort((a, b) => a.completedAt - b.completedAt)
    .slice(-256);
}

export function mergePrefillRuns(current: PrefillRun[], incoming: PrefillRun[]): PrefillRun[] {
  const merged = new Map(current.map((run) => [run.runId, run]));
  for (const run of incoming) {
    const previous = merged.get(run.runId);
    if (
      previous &&
      (previous.sessionId !== run.sessionId ||
        previous.daemonInstanceId !== run.daemonInstanceId ||
        previous.snapshot.sequence > run.snapshot.sequence ||
        (!isPrefillRunActive(previous) && isPrefillRunActive(run)))
    )
      continue;
    merged.set(
      run.runId,
      previous
        ? {
            ...run,
            progress:
              (previous.progress?.sequence ?? -1) > (run.progress?.sequence ?? -1)
                ? previous.progress
                : run.progress,
            cancelRequested:
              isPrefillRunActive(run) && (previous.cancelRequested || run.cancelRequested)
          }
        : run
    );
  }
  return [...merged.values()].sort(
    (a, b) =>
      a.snapshot.startedAt.localeCompare(b.snapshot.startedAt) || a.runId.localeCompare(b.runId)
  );
}

export function getPrefillRunProgress(run: PrefillRun): PrefillProgress {
  const { snapshot, progress } = run;
  const item = snapshot.currentItem;
  const terminal = !isPrefillRunActive(run);
  return {
    state: terminal
      ? snapshot.state
      : run.recovering
        ? 'reconnecting'
        : (progress?.state ?? snapshot.state),
    currentAppId: item?.appId ?? progress?.currentAppId ?? '',
    currentAppName: item?.name ?? progress?.currentAppName,
    percentComplete:
      item?.totalBytes && item.totalBytes > 0
        ? Math.min(100, (100 * item.bytesTransferred) / item.totalBytes)
        : (progress?.percentComplete ?? 0),
    bytesDownloaded: item?.bytesTransferred ?? progress?.bytesDownloaded ?? 0,
    totalBytes: item?.totalBytes ?? progress?.totalBytes ?? 0,
    bytesPerSecond: run.recovering || terminal ? 0 : (progress?.bytesPerSecond ?? 0),
    elapsedSeconds: progress?.elapsedSeconds ?? 0,
    message: progress?.message,
    totalApps: snapshot.totalApps,
    updatedApps: snapshot.completedApps,
    alreadyUpToDate: snapshot.cachedApps,
    failedApps: snapshot.failedApps,
    cancelledApps: snapshot.cancelledApps,
    skippedApps: snapshot.skippedApps,
    operationId: run.runId,
    daemonInstanceId: run.daemonInstanceId,
    sequence: snapshot.sequence
  };
}

export function applyPrefillRunProgress(
  runs: PrefillRun[],
  progress: PrefillProgress
): PrefillRun[] {
  return runs.map((run) => {
    if (
      run.runId !== progress.operationId ||
      run.daemonInstanceId !== progress.daemonInstanceId ||
      progress.sequence === undefined ||
      progress.sequence <= run.snapshot.sequence ||
      !isPrefillRunActive(run)
    )
      return run;
    return {
      ...run,
      progress,
      snapshot: {
        ...run.snapshot,
        sequence: progress.sequence,
        state: progress.state,
        reason: progress.reason ?? run.snapshot.reason,
        bytesTransferred: progress.totalBytesTransferred ?? run.snapshot.bytesTransferred,
        updatedAt: progress.updatedAt ?? run.snapshot.updatedAt,
        totalApps: progress.totalApps ?? run.snapshot.totalApps,
        completedApps: progress.updatedApps ?? run.snapshot.completedApps,
        cachedApps: progress.alreadyUpToDate ?? run.snapshot.cachedApps,
        failedApps: progress.failedApps ?? run.snapshot.failedApps,
        skippedApps: progress.skippedApps ?? run.snapshot.skippedApps,
        cancelledApps: progress.cancelledApps ?? run.snapshot.cancelledApps,
        currentItem:
          progress.currentAppId && progress.currentAppId !== '0'
            ? {
                appId: String(progress.currentAppId),
                name: progress.currentAppName,
                state: progress.state,
                sequence: progress.sequence,
                bytesTransferred: progress.bytesDownloaded,
                totalBytes: progress.totalBytes
              }
            : null
      }
    };
  });
}

export function canStartPrefill(session: {
  features?: string[];
  maxConcurrentRuns?: number;
  activeRunCount?: number;
  recovering?: boolean;
  isPrefilling?: boolean;
  runs?: PrefillRun[];
}): boolean {
  if (session.recovering || session.runs?.some((run) => isPrefillRunActive(run) && run.recovering))
    return false;
  if (!supportsConcurrentPrefill(session)) return !session.isPrefilling;
  return (
    Math.max(session.activeRunCount ?? 0, session.runs?.filter(isPrefillRunActive).length ?? 0) <
    (session.maxConcurrentRuns ?? 1)
  );
}

export interface BackgroundCompletion {
  completedAt: string;
  message: string;
  duration: number;
}

export function supportsConcurrentPrefill(
  session: { features?: string[] } | null | undefined
): boolean {
  return [
    'concurrentPrefill',
    'operationProgress',
    'targetedCancel',
    'inlineSelection',
    'activeOperations'
  ].every((feature) => session?.features?.includes(feature));
}

export interface CachedAnimationItem {
  appId: string;
  appName?: string;
  totalBytes: number;
  /**
   * Snapshot of the running job counts at enqueue time so the cached-game animation can keep the
   * two-tier "Game X of N" overall bar advancing (the animation builds a fresh PrefillProgress and
   * would otherwise starve those fields back to 0). V11.
   */
  expectedAppCount?: number;
  updatedApps?: number;
  alreadyUpToDate?: number;
}
