import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import './StatusCheckSection.css';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { TogglePill } from '@components/ui/TogglePill';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService, {
  type StatusCheckDomainGroup,
  type StatusCheckDomainsSource,
  type StatusCheckResolverMode,
  type StatusCheckServiceResult,
  type StatusCheckStatusResponse
} from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { getErrorMessage, isAbortError } from '@utils/error';
import VerdictCard from './VerdictCard';
import ServiceResultsList from './ServiceResultsList';
import ClientProbeCard from './ClientProbeCard';
import TestDomainCard from './TestDomainCard';
import DomainSourceFooter from './DomainSourceFooter';
import { useClientProbe } from './useClientProbe';
import { prefersReducedMotion } from './helpers';
import { isVisibleWithProblemsOnly } from './contentPathHelpers';
import { RESOLVER_MODE_OPTIONS } from './constants';
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
  StatusCheckProgressEvent
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
  const [runError, setRunError] = useState<string | null>(null);
  const [problemsOnly, setProblemsOnly] = useState(false);
  const [expandedServices, setExpandedServices] = useState<Set<string>>(new Set());
  const [refreshingDomains, setRefreshingDomains] = useState(false);
  const [refreshError, setRefreshError] = useState<string | null>(null);

  const serviceRowRefs = useRef<Map<string, HTMLDivElement>>(new Map());
  // Guards against out-of-order SignalR events: a delayed progress event arriving after its
  // sweep completed, or a stale complete event from a previous sweep, must not flip run state.
  // Only the most recently finished sweep ever needs to be ignored - the server runs one sweep
  // at a time, so there's never more than one "stale" operation id in flight.
  const currentOperationRef = useRef<string | null>(getCachedStatus()?.operationId ?? null);
  const lastFinishedOperationRef = useRef<string | null>(null);
  const { state: probeState, retry: retryProbe } = useClientProbe();

  const reloadDomains = useCallback(async (domainsSource: StatusCheckDomainsSource) => {
    setStatus((previous) => (previous ? { ...previous, domainsSource } : previous));
    const domains = await ApiService.getCacheDomains();
    setDomainGroups(domains.services);
  }, []);

  const loadAll = useCallback(async (signal?: AbortSignal) => {
    const [statusResult, domainsResult] = await Promise.allSettled([
      ApiService.getStatusCheck(signal),
      ApiService.getCacheDomains(signal)
    ]);
    if (signal?.aborted) return;
    if (statusResult.status === 'fulfilled') {
      setStatus(statusResult.value);
      setIsRunning(statusResult.value.isRunning);
      currentOperationRef.current = statusResult.value.operationId;
      setStatusError(null);
    } else if (!isAbortError(statusResult.reason)) {
      setStatusError(getErrorMessage(statusResult.reason));
    }
    // A failed domain list is non-fatal: the test dropdown renders its unavailable state.
    if (domainsResult.status === 'fulfilled') {
      setDomainGroups(domainsResult.value.services);
    }
    setIsLoading(false);
  }, []);

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
      if (lastFinishedOperationRef.current === event.operationId) return;
      currentOperationRef.current = event.operationId;
      setIsRunning(true);
      setProgress(event);
    };
    const handleComplete = (event: StatusCheckCompleteEvent): void => {
      lastFinishedOperationRef.current = event.operationId;
      if (currentOperationRef.current && currentOperationRef.current !== event.operationId) {
        return; // a previous sweep's completion arriving late - not the run we're showing
      }
      currentOperationRef.current = null;
      setIsRunning(false);
      setProgress(null);
      if (event.success && event.result) {
        setRunError(null);
        setStatus((previous) => ({
          lastResult: event.result,
          domainsSource: previous?.domainsSource ?? null,
          isRunning: false,
          operationId: null,
          resolverMode: previous?.resolverMode ?? 'auto'
        }));
      } else {
        setRunError(event.error ?? t(`${keys}.sweepDidNotFinish`));
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
  }, [on, off, t, reloadDomains]);

  // A reconnect can swallow the completion event of a sweep that finished while
  // the socket was down - resync from the server whenever the connection returns.
  const wasDisconnectedRef = useRef(false);
  useEffect(() => {
    if (!isConnected) {
      wasDisconnectedRef.current = true;
      return;
    }
    if (wasDisconnectedRef.current) {
      wasDisconnectedRef.current = false;
      void loadAll();
    }
  }, [isConnected, loadAll]);

  const handleRun = useCallback(async () => {
    setRunError(null);
    try {
      const run = await ApiService.runStatusCheck();
      currentOperationRef.current = run.operationId;
      setIsRunning(true);
      setProgress(null);
    } catch (error) {
      // A 409 means a sweep is already running elsewhere - resync instead of erroring.
      try {
        const current = await ApiService.getStatusCheck();
        setStatus(current);
        setIsRunning(current.isRunning);
        currentOperationRef.current = current.operationId;
        if (!current.isRunning) setRunError(getErrorMessage(error));
      } catch {
        setRunError(getErrorMessage(error));
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
      setRunError(null);
      setStatus((previous) => (previous ? { ...previous, resolverMode: mode } : previous));
      try {
        const result = await ApiService.setStatusCheckResolverMode(mode);
        setStatus((previous) =>
          previous ? { ...previous, resolverMode: result.resolverMode } : previous
        );
      } catch (error) {
        setRunError(getErrorMessage(error));
        // Persist failed - resync the authoritative mode instead of leaving the optimistic one.
        try {
          const current = await ApiService.getStatusCheck();
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

  const toggleService = useCallback((service: string) => {
    setExpandedServices((previous) => {
      const next = new Set(previous);
      if (next.has(service)) {
        next.delete(service);
      } else {
        next.add(service);
      }
      return next;
    });
  }, []);

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
      setExpandedServices((previous) => new Set(previous).add(service));
      // Let the filter change / accordion expansion mount before scrolling.
      setTimeout(() => {
        serviceRowRefs.current.get(service)?.scrollIntoView({
          behavior: prefersReducedMotion() ? 'auto' : 'smooth',
          block: 'start'
        });
      }, 60);
    },
    [sortedServices, problemsOnly, status?.lastResult?.contentReport]
  );

  const handleRefreshDomains = useCallback(async () => {
    setRefreshingDomains(true);
    setRefreshError(null);
    try {
      const result = await ApiService.refreshCacheDomains();
      await reloadDomains(result.domainsSource);
    } catch (error) {
      setRefreshError(getErrorMessage(error));
    } finally {
      setRefreshingDomains(false);
    }
  }, [reloadDomains]);

  const sectionShell = (children: React.ReactNode): React.ReactElement => (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-status-check"
      aria-labelledby="tab-status-check"
    >
      {children}
    </div>
  );

  if (isLoading) {
    return sectionShell(
      <div className="flex justify-center py-12">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (statusError && !status) {
    return sectionShell(
      <div className="flex flex-col items-center gap-3 py-8 text-center">
        <p className="text-sm text-themed-secondary">
          {t(`${keys}.loadFailed`, { error: statusError })}
        </p>
        <Button variant="filled" color="blue" size="sm" onClick={() => void loadAll()}>
          {t(`${keys}.retry`)}
        </Button>
      </div>
    );
  }

  const lastResult = status?.lastResult ?? null;

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
        <Alert color="red" className="mb-4">
          {t(`${keys}.loadFailed`, { error: statusError })}
        </Alert>
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
            <div className="flex items-center justify-between gap-3 flex-wrap mb-3">
              <div>
                <h3 className="integrations-group-label caps-label">{t(`${keys}.serverLane`)}</h3>
              </div>
              {lastResult && (
                <TogglePill
                  size="sm"
                  active={problemsOnly}
                  onClick={() => setProblemsOnly((previous) => !previous)}
                >
                  {t(`${keys}.showOnlyProblems`)}
                </TogglePill>
              )}
            </div>
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
          <h3 className="integrations-group-label caps-label mb-3">{t(`${keys}.deviceLane`)}</h3>
          <ClientProbeCard state={probeState} onRetry={retryProbe} />
        </section>

        <section>
          <h3 className="integrations-group-label caps-label mb-3">{t(`${keys}.testLane`)}</h3>
          <TestDomainCard groups={domainGroups} />
        </section>

        <DomainSourceFooter
          source={status?.domainsSource ?? null}
          onRefresh={() => void handleRefreshDomains()}
          refreshing={refreshingDomains}
          refreshError={refreshError}
        />
      </div>
    </>
  );
};

export default StatusCheckSection;
