import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import './StatusCheckSection.css';
import { useTranslation } from 'react-i18next';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { TogglePill } from '@components/ui/TogglePill';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { LoadingState } from '@components/ui/ManagerCard';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import { GroupHeading } from '@components/ui/GroupHeading';
import { TabPanel } from '@components/features/management/TabPanel';
import ApiService, {
  type StatusCheckDomainGroup,
  type StatusCheckDomainsSource,
  type StatusCheckResolverMode,
  type StatusCheckServiceResult,
  type StatusCheckStatusResponse
} from '@services/api.service';
import { ApiError } from '@services/apiError';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useMockMode } from '@contexts/useMockMode';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useSelectionSet } from '@hooks/useSelectionSet';
import { getErrorMessage, isAbortError } from '@utils/error';
import VerdictCard from './VerdictCard';
import ServiceResultsList from './ServiceResultsList';
import ClientProbeCard from './ClientProbeCard';
import TestDomainCard from './TestDomainCard';
import DomainSourceFooter from './DomainSourceFooter';
import { useClientProbe } from './useClientProbe';
import { isProbeHostHeartbeatVerified, prefersReducedMotion } from './helpers';
import { isVisibleWithProblemsOnly } from './contentPathHelpers';
import { CLIENT_PROBE_HOST, RESOLVER_MODE_OPTIONS } from './constants';
import {
  getCachedDomainGroups,
  getCachedStatus,
  setCachedDomainGroups,
  setCachedStatus
} from './statusCheckCache';
import type {
  CacheDomainsRefreshedEvent,
  RibbonSegment,
  RibbonSegmentStatus,
  StatusCheckCompleteEvent,
  StatusCheckProgressEvent,
  StatusCheckRunError
} from './types';

// Problems first; unverified (can't compare - hero already warns) before healthy;
// disabled services (intentionally not cached) sort last.
const SERVICE_STATUS_WEIGHT: Record<StatusCheckServiceResult['status'], number> = {
  unresolved: 0,
  partial: 1,
  unverified: 2,
  resolved: 3,
  disabled: 4
};

const StatusCheckSection: React.FC = () => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';
  const { on, off, isConnected } = useSignalR();
  const { mockMode } = useMockMode();

  // Seed from the module-level cache so a reopen paints the last result instantly
  // (stale-while-revalidate): no full-page spinner when we already have a cached snapshot.
  const [status, setStatus] = useState<StatusCheckStatusResponse | null>(() => getCachedStatus());
  const [statusError, setStatusError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(() => getCachedStatus() === null);
  const [domainGroups, setDomainGroups] = useState<StatusCheckDomainGroup[] | null>(() =>
    getCachedDomainGroups()
  );
  // Seed isRunning FALSE, never from the cached snapshot: a settled first paint is the whole
  // point (crit 10). The authoritative GET + SignalR progress establish "running" within a
  // round-trip if a sweep really is in flight, so seeding from a possibly-stale cache would
  // only risk a phantom scan-replay on reopen (and a stuck one if that GET fails).
  const [isRunning, setIsRunning] = useState(false);
  const [progress, setProgress] = useState<StatusCheckProgressEvent | null>(null);
  const [runError, setRunError] = useState<StatusCheckRunError | null>(null);
  const [problemsOnly, setProblemsOnly] = useState(false);
  const { selected: expandedServices, toggle: toggleService, setMany } = useSelectionSet<string>();
  const [refreshingDomains, setRefreshingDomains] = useState(false);
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const [reloadError, setReloadError] = useState<string | null>(null);

  const serviceRowRefs = useRef<Map<string, HTMLDivElement>>(new Map());
  // Guards against out-of-order SignalR events: a delayed progress event arriving after its
  // sweep completed, or a stale complete event from a previous sweep, must not flip run state.
  // Every finished id is kept, because one slow status read can outlive several sweeps.
  // Starts empty rather than from the cached status: that sweep may have ended while the page was
  // closed, and its id would then hide the ending of the next sweep. The mount read sets it.
  const currentOperationRef = useRef<string | null>(null);
  const lastFinishedOperationRef = useRef<string | null>(null);
  const finishedOperationIdsRef = useRef<Set<string>>(new Set());
  // Counts the newer states the page applied (a run, a completion, a domain reload, a mode change).
  // A status read sent before one of them is older than the page, so it is read again rather than
  // applied.
  const statusVersionRef = useRef(0);
  const { state: probeState, retry: retryProbe } = useClientProbe();

  const reloadDomains = useCallback(async (domainsSource: StatusCheckDomainsSource) => {
    statusVersionRef.current += 1;
    setStatus((previous) => (previous ? { ...previous, domainsSource } : previous));
    try {
      const domains = await ApiService.getCacheDomains();
      setDomainGroups(domains.services);
      setReloadError(null);
    } catch (error) {
      setReloadError(getErrorMessage(error));
    }
  }, []);

  const loadAll = useCallback(
    async (signal?: AbortSignal) => {
      // Mock mode has no resolver or cache host behind it, so the section shows its empty state
      // rather than a real machine's DNS and reachability answers.
      if (mockMode) {
        setStatus(null);
        setDomainGroups([]);
        setIsRunning(false);
        setStatusError(null);
        setIsLoading(false);
        return;
      }
      const version = statusVersionRef.current;
      const [statusResult, domainsResult] = await Promise.allSettled([
        ApiService.getStatusCheck(signal),
        ApiService.getCacheDomains(signal)
      ]);
      if (signal?.aborted) return;
      if (statusVersionRef.current !== version) return loadAll(signal);
      if (statusResult.status === 'fulfilled') {
        // Read before that sweep's completion arrived and answered after it: the completion is newer.
        if (
          !(
            statusResult.value.isRunning &&
            statusResult.value.operationId !== null &&
            finishedOperationIdsRef.current.has(statusResult.value.operationId)
          )
        ) {
          setStatus(statusResult.value);
          setIsRunning(statusResult.value.isRunning);
          currentOperationRef.current = statusResult.value.operationId;
        }
        setStatusError(null);
      } else if (!isAbortError(statusResult.reason)) {
        setStatusError(getErrorMessage(statusResult.reason));
      }
      // A failed domain list is non-fatal: the test dropdown renders its unavailable state.
      if (domainsResult.status === 'fulfilled') {
        setDomainGroups(domainsResult.value.services);
        setReloadError(null);
      }
      setIsLoading(false);
    },
    [mockMode]
  );

  useEffect(() => {
    const controller = new AbortController();
    void loadAll(controller.signal);
    return () => controller.abort();
  }, [loadAll]);

  // Keep the seed cache fresh on EVERY status change - GET refresh, SignalR completion, and
  // resolver-mode change all flow through setStatus - so the next reopen paints the newest data.
  useEffect(() => {
    if (status) setCachedStatus(status);
  }, [status]);

  useEffect(() => {
    if (domainGroups) setCachedDomainGroups(domainGroups);
  }, [domainGroups]);

  useEffect(() => {
    const handleProgress = (event: StatusCheckProgressEvent): void => {
      if (finishedOperationIdsRef.current.has(event.operationId)) return;
      currentOperationRef.current = event.operationId;
      setIsRunning(true);
      setProgress(event);
    };
    const handleComplete = (event: StatusCheckCompleteEvent): void => {
      lastFinishedOperationRef.current = event.operationId;
      finishedOperationIdsRef.current.add(event.operationId);
      if (currentOperationRef.current && currentOperationRef.current !== event.operationId) {
        return; // a previous sweep's completion arriving late - not the run we're showing
      }
      statusVersionRef.current += 1;
      currentOperationRef.current = null;
      setIsRunning(false);
      setProgress(null);
      if (event.success) {
        setRunError(null);
        setStatus((previous) => ({
          lastResult: event.result,
          domainsSource: previous?.domainsSource ?? null,
          isRunning: false,
          operationId: null,
          resolverMode: previous?.resolverMode ?? 'auto'
        }));
      } else {
        setRunError(
          event.canceled
            ? { stageKey: event.stageKey, canceled: true }
            : { stageKey: event.stageKey, context: event.context ?? {} }
        );
      }
    };
    const handleDomainsRefreshed = (event: CacheDomainsRefreshedEvent): void => {
      void reloadDomains(event.domainsSource);
    };
    on('StatusCheckProgress', handleProgress);
    on('StatusCheckComplete', handleComplete);
    on('CacheDomainsRefreshed', handleDomainsRefreshed);
    return () => {
      off('StatusCheckProgress', handleProgress);
      off('StatusCheckComplete', handleComplete);
      off('CacheDomainsRefreshed', handleDomainsRefreshed);
    };
  }, [on, off, reloadDomains]);

  // A reconnect can swallow the completion event of a sweep that finished while
  // the socket was down - resync from the server whenever the connection returns.
  useReconnectRefetch(isConnected, loadAll);

  const handleRun = useCallback(async () => {
    const finishedAtClick = lastFinishedOperationRef.current;
    try {
      const run = await ApiService.runStatusCheck();
      statusVersionRef.current += 1;
      // The sweep starts before the reply is written, so its completion can arrive first.
      if (finishedOperationIdsRef.current.has(run.operationId)) return;
      setRunError(null);
      currentOperationRef.current = run.operationId;
      setIsRunning(true);
      setProgress(null);
    } catch (error) {
      // A 409 means a sweep is already running elsewhere: show it, or its result if it has finished.
      const conflict = error instanceof ApiError && error.kind === 'conflict';
      try {
        const current = await ApiService.getStatusCheck();
        statusVersionRef.current += 1;
        // Read before that sweep's completion arrived and answered after it: the completion is newer.
        if (
          current.isRunning &&
          current.operationId !== null &&
          finishedOperationIdsRef.current.has(current.operationId)
        )
          return;
        setStatus(current);
        setIsRunning(current.isRunning);
        currentOperationRef.current = current.operationId;
        // The other sweep ended while this request was out, and its completion drew its own ending.
        if (conflict && !current.isRunning && lastFinishedOperationRef.current !== finishedAtClick)
          return;
        setRunError(
          !current.isRunning && !conflict ? { stageKey: `${keys}.sweepFailed`, error } : null
        );
      } catch {
        setRunError({ stageKey: `${keys}.sweepFailed`, error });
      }
    }
  }, []);

  const resolverMode: StatusCheckResolverMode = status?.resolverMode ?? 'auto';

  const resolverModeOptions = useMemo(
    () =>
      RESOLVER_MODE_OPTIONS.map((option) => ({
        value: option.value,
        label: t(`${keys}.resolverMode.${option.labelKey}`),
        tooltip: t(`${keys}.resolverMode.${option.tooltipKey}`),
        disabled: isRunning
      })),
    [t, isRunning]
  );

  // The mode only steers the NEXT sweep, so persist it and immediately kick off a
  // fresh check (unless one is already running) so the user sees the effect.
  const handleResolverModeChange = useCallback(
    async (mode: StatusCheckResolverMode) => {
      if (mode === resolverMode) return;
      statusVersionRef.current += 1;
      setStatus((previous) => (previous ? { ...previous, resolverMode: mode } : previous));
      try {
        const result = await ApiService.setStatusCheckResolverMode(mode);
        setRunError(null);
        statusVersionRef.current += 1;
        setStatus((previous) =>
          previous ? { ...previous, resolverMode: result.resolverMode } : previous
        );
      } catch (error) {
        setRunError({ stageKey: `${keys}.resolverMode.saveFailed`, error });
        // Persist failed - resync the authoritative mode instead of leaving the optimistic one.
        try {
          const current = await ApiService.getStatusCheck();
          statusVersionRef.current += 1;
          setStatus(current);
        } catch {
          /* keep the existing error visible */
        }
        return;
      }
      if (!isRunning) await handleRun();
    },
    [resolverMode, isRunning, handleRun]
  );

  const sortedServices = useMemo(() => {
    const services = status?.lastResult?.services ?? [];
    return [...services].sort(
      (a, b) =>
        SERVICE_STATUS_WEIGHT[a.status] - SERVICE_STATUS_WEIGHT[b.status] ||
        a.service.localeCompare(b.service)
    );
  }, [status?.lastResult]);

  const ribbonSegments = useMemo<RibbonSegment[]>(() => {
    if (isRunning) {
      // Live sweep: the backend walks services in list order and names the one it is on in
      // every progress event - fill segments by position relative to it. (completedDomains
      // cannot drive the fill: its denominator excludes DISABLE_*-skipped services, which the
      // list rendered here still contains.)
      const serviceNames =
        domainGroups?.map((group) => group.name) ??
        status?.lastResult?.services.map((service) => service.service) ??
        [];
      const currentIndex = progress?.currentService
        ? serviceNames.indexOf(progress.currentService)
        : -1;
      return serviceNames.map((service, index) => {
        let segmentStatus: RibbonSegmentStatus;
        if (currentIndex < 0) {
          segmentStatus = 'pending';
        } else if (index < currentIndex) {
          segmentStatus = 'scanned';
        } else if (index === currentIndex) {
          segmentStatus = 'scanning';
        } else {
          segmentStatus = 'pending';
        }
        return { service, status: segmentStatus };
      });
    }
    return sortedServices.map((service) => ({ service: service.service, status: service.status }));
  }, [isRunning, progress, domainGroups, sortedServices, status?.lastResult]);

  const registerServiceRef = useCallback((service: string, element: HTMLDivElement | null) => {
    if (element) {
      serviceRowRefs.current.set(service, element);
    } else {
      serviceRowRefs.current.delete(service);
    }
  }, []);

  const handleRibbonSegmentClick = useCallback(
    (service: string) => {
      const target = sortedServices.find((entry) => entry.service === service);
      if (!target) return;
      if (problemsOnly && !isVisibleWithProblemsOnly(target, status?.lastResult?.contentReport)) {
        setProblemsOnly(false);
      }
      setMany([service], true);
      // Let the filter change / accordion expansion mount before scrolling.
      setTimeout(() => {
        serviceRowRefs.current.get(service)?.scrollIntoView({
          behavior: prefersReducedMotion() ? 'auto' : 'smooth',
          block: 'start'
        });
      }, 60);
    },
    [sortedServices, problemsOnly, status?.lastResult?.contentReport, setMany]
  );

  const handleRefreshDomains = useCallback(async () => {
    setRefreshingDomains(true);
    try {
      const result = await ApiService.refreshCacheDomains();
      setRefreshError(null);
      await reloadDomains(result.domainsSource);
    } catch (error) {
      setRefreshError(getErrorMessage(error));
    } finally {
      setRefreshingDomains(false);
    }
  }, [reloadDomains]);

  const sectionShell = (children: React.ReactNode): React.ReactElement => (
    <TabPanel tabId="status-check">{children}</TabPanel>
  );

  if (isLoading) {
    return sectionShell(<LoadingState shape="status" rows={4} />);
  }

  if (statusError && !status) {
    return sectionShell(
      <ErrorBlock
        title={t(`${keys}.loadFailed`)}
        message={statusError}
        retryLabel={t('common.retry')}
        onRetry={() => void loadAll()}
      />
    );
  }

  const lastResult = status?.lastResult ?? null;
  const hostHeartbeatVerified = isProbeHostHeartbeatVerified(lastResult, CLIENT_PROBE_HOST);

  // Rendered as a quiet toolbar at the top of the verdict card; each option's
  // tooltip explains its strategy, so no hint paragraph floats in the layout.
  const resolverControl = status ? (
    <>
      <span className="status-check-resolver-label caps-label caps-label--sm">
        {t(`${keys}.resolverMode.label`)}
      </span>
      <SegmentedControl
        size="sm"
        showLabels
        options={resolverModeOptions}
        value={resolverMode}
        onChange={(value) => void handleResolverModeChange(value as StatusCheckResolverMode)}
      />
    </>
  ) : null;

  return sectionShell(
    <>
      {statusError && status && (
        <ErrorBlock
          title={t(`${keys}.loadFailed`)}
          message={statusError}
          retryLabel={t('common.retry')}
          onRetry={() => void loadAll()}
          className="mb-4"
        />
      )}
      <div className="space-y-8">
        <section>
          <VerdictCard
            lastResult={lastResult}
            isRunning={isRunning}
            progress={progress}
            runError={runError}
            ribbonSegments={ribbonSegments}
            ribbonInteractive={!isRunning && sortedServices.length > 0}
            onRibbonSegmentClick={handleRibbonSegmentClick}
            onRun={() => void handleRun()}
            resolverControl={resolverControl}
          />
        </section>

        {(lastResult || isRunning) && (
          <section>
            <GroupHeading
              label={t(`${keys}.serverLane`)}
              actions={
                <div className="flex items-center gap-2">
                  {lastResult && (
                    <TogglePill
                      size="md"
                      active={problemsOnly}
                      onClick={() => setProblemsOnly((previous) => !previous)}
                    >
                      {t(`${keys}.showOnlyProblems`)}
                    </TogglePill>
                  )}
                  <AccordionGroupToggle />
                </div>
              }
            />
            {lastResult ? (
              <ServiceResultsList
                services={sortedServices}
                expandedServices={expandedServices}
                onToggle={toggleService}
                problemsOnly={problemsOnly}
                contentReport={lastResult.contentReport}
                registerRef={registerServiceRef}
              />
            ) : (
              <p className="text-sm text-themed-muted">{t(`${keys}.serverLanePending`)}</p>
            )}
          </section>
        )}

        <section>
          <GroupHeading label={t(`${keys}.deviceLane`)} />
          <ClientProbeCard
            state={probeState}
            onRetry={retryProbe}
            hostHeartbeatVerified={hostHeartbeatVerified}
          />
        </section>

        <section>
          <GroupHeading label={t(`${keys}.testLane`)} />
          <TestDomainCard groups={domainGroups} />
        </section>

        <DomainSourceFooter
          source={status?.domainsSource ?? null}
          onRefresh={() => void handleRefreshDomains()}
          refreshing={refreshingDomains}
          refreshError={refreshError}
          reloadError={reloadError}
        />
      </div>
    </>
  );
};

export default StatusCheckSection;
