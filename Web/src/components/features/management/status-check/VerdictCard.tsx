import React from 'react';
import '../managementSectionContent.css';
import './StatusCheckSection.css';
import { useTranslation } from 'react-i18next';
import { Activity, AlertTriangle, CheckCircle2, XCircle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpPopover } from '@components/ui/HelpPopover';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import type {
  StatusCheckResult,
  StatusCheckServiceResult,
  StatusCheckSummary
} from '@services/api.service';
import ResolutionRibbon from './ResolutionRibbon';
import ContentPathSummary from './ContentPathSummary';
import { splitExamples } from './helpers';
import { formatServiceLabel } from '@utils/serviceDisplayName';
import { translateStageKeyMessage } from '@utils/stageKeyMessage';
import { getErrorMessage } from '@utils/error';
import type { RibbonSegment, StatusCheckProgressEvent, StatusCheckRunError } from './types';

interface VerdictCardProps {
  lastResult: StatusCheckResult | null;
  isRunning: boolean;
  progress: StatusCheckProgressEvent | null;
  runError: StatusCheckRunError | null;
  ribbonSegments: RibbonSegment[];
  ribbonInteractive: boolean;
  onRibbonSegmentClick: (service: string) => void;
  onRun: () => void;
  /** The resolver-mode picker, drawn beside Run Check with its label. */
  resolverControl?: React.ReactNode;
}

/** One row of the breakdown under the verdict: a dot, a count and the services it holds. */
interface VerdictBreakdownRow {
  // Every service status that needs a look, plus the domain status that names the worst case, so
  // a new status on the wire shows up here as a type error.
  id: Exclude<StatusCheckServiceResult['status'], 'resolved'> | 'mismatched';
  /** The shared `.status-dot` tone class. */
  tone: 'warning' | 'error' | 'info' | 'idle';
  services: StatusCheckServiceResult[];
  label: string;
}

// The non-running verdict category - drives both the headline text and the glyph so the two
// can never drift (allDisabled deliberately keeps its own text but folds to the neutral glyph,
// same as everything else with nothing to report on).
type VerdictKind = 'cantVerify' | 'allDisabled' | 'all' | 'none' | 'partial';

const resolveVerdictKind = (
  summary: StatusCheckSummary,
  activeTotal: number,
  cantVerify: boolean
): VerdictKind => {
  if (cantVerify) return 'cantVerify';
  if (activeTotal === 0) return 'allDisabled';
  if (summary.resolvedServices === activeTotal) return 'all';
  if (summary.resolvedServices === 0) return 'none';
  return 'partial';
};

// How many example service chips to show before collapsing the rest into "+N more".
const CHIP_LIMIT = 3;
const VerdictCard: React.FC<VerdictCardProps> = ({
  lastResult,
  isRunning,
  progress,
  runError,
  ribbonSegments,
  ribbonInteractive,
  onRibbonSegmentClick,
  onRun,
  resolverControl
}) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';
  const lastCheckedTime = useFormattedDateTime(lastResult?.completedAtUtc);
  const runErrorText = runError
    ? 'canceled' in runError
      ? t(runError.stageKey)
      : t(
          runError.stageKey,
          'error' in runError ? { error: getErrorMessage(runError.error) } : runError.context
        )
    : null;
  // A canceled sweep is not an error, so its sentence sits in the gray box.
  const runErrorColor = runError && 'canceled' in runError ? 'neutral' : 'red';

  // The picker steers the next run, so in every state it sits beside Run Check.
  const resolverChoice = resolverControl ? (
    <div className="cluster min-w-0">
      <span className="text-xs text-themed-muted whitespace-nowrap">
        {t(`${keys}.resolverMode.label`)}
      </span>
      {resolverControl}
    </div>
  ) : null;

  const summary = lastResult?.summary ?? null;
  // Disabled services are intentionally not cached - excluded from verdict math entirely.
  const activeTotal = summary
    ? summary.resolvedServices + summary.partialServices + summary.unresolvedServices
    : 0;
  // v1.4: domains that answered the heartbeat are verified LIVE, independent of any baseline.
  const verifiedDomains = lastResult
    ? lastResult.services.reduce(
        (count, service) =>
          count + service.domains.filter((domain) => domain.heartbeatVerified).length,
        0
      )
    : 0;
  // v1.3/v1.4: the "can't verify" lead is reserved for the nothing-to-go-on case - no expected
  // cache IP AND nothing heartbeat-verified. With live verifications the normal verdict speaks.
  const cantVerify =
    !!lastResult && lastResult.expectedCacheIps.length === 0 && verifiedDomains === 0;
  const failingServices = lastResult
    ? lastResult.services.filter(
        (service) => service.status === 'partial' || service.status === 'unresolved'
      )
    : [];
  // Bucketing keys on the service's OWN status, never just on the failure type of its worst
  // domain - a "partial" service (some domains resolving fine) must never fall into the "not
  // resolving at all" bucket just because none of its failing domains happen to be "mismatched".
  const mismatchedServices = failingServices.filter((service) =>
    service.domains.some((domain) => domain.status === 'mismatched')
  );
  const nonMismatchedFailingServices = failingServices.filter(
    (service) => !mismatchedServices.includes(service)
  );
  const unresolvedOnlyServices = nonMismatchedFailingServices.filter(
    (service) => service.status === 'unresolved'
  );
  const partialOnlyServices = nonMismatchedFailingServices.filter(
    (service) => service.status === 'partial'
  );

  // Empty state: never run and not running now - render the invitation, not a blank card.
  if (!lastResult && !isRunning) {
    return (
      <Card>
        <div className="status-check-verdict-header">
          <div className="status-check-verdict-lead">
            <div className="status-check-glyph status-check-glyph--neutral">
              <Activity className="w-5 h-5" />
            </div>
            <div className="status-check-verdict-copy">
              <h3 className="status-check-verdict-line">{t(`${keys}.emptyTitle`)}</h3>
              <p className="status-check-verdict-sub">{t(`${keys}.emptyBody`)}</p>
            </div>
          </div>
          <div className="status-check-verdict-actions">
            {resolverChoice}
            <Button variant="filled" color="run" size="md" onClick={onRun}>
              {t(`${keys}.runCheck`)}
            </Button>
          </div>
        </div>
        {runErrorText && (
          <Alert color={runErrorColor} className="mt-3">
            {runErrorText}
          </Alert>
        )}
      </Card>
    );
  }

  const verdictKind: 'running' | VerdictKind | null = isRunning
    ? 'running'
    : summary
      ? resolveVerdictKind(summary, activeTotal, cantVerify)
      : null;

  const verdictLine =
    verdictKind === 'running'
      ? progress && progress.totalDomains > 0
        ? t(`${keys}.sweepProgress`, {
            completed: progress.completedDomains,
            total: progress.totalDomains
          })
        : t(`${keys}.sweepStarting`)
      : verdictKind === 'cantVerify'
        ? t(`${keys}.verdictUnverified`)
        : verdictKind === 'allDisabled'
          ? t(`${keys}.verdictAllDisabled`)
          : verdictKind === 'all'
            ? t(`${keys}.verdictAll`, { count: activeTotal })
            : verdictKind === 'none'
              ? t(`${keys}.verdictNone`, { count: activeTotal })
              : verdictKind === 'partial'
                ? t(`${keys}.verdictPartial`, {
                    resolved: summary?.resolvedServices ?? 0,
                    count: activeTotal
                  })
                : '';

  // running/allDisabled/no-summary all render the same neutral glyph - nothing to report on yet.
  const glyph =
    verdictKind === 'cantVerify' ? (
      <div className="status-check-glyph status-check-glyph--warning">
        <AlertTriangle className="w-5 h-5" />
      </div>
    ) : verdictKind === 'running' || verdictKind === 'allDisabled' || verdictKind === null ? (
      <div className="status-check-glyph status-check-glyph--neutral">
        <Activity className="w-5 h-5" />
      </div>
    ) : verdictKind === 'all' ? (
      <div className="status-check-glyph status-check-glyph--success">
        <CheckCircle2 className="w-5 h-5" />
      </div>
    ) : verdictKind === 'none' ? (
      <div className="status-check-glyph status-check-glyph--error">
        <XCircle className="w-5 h-5" />
      </div>
    ) : (
      <div className="status-check-glyph status-check-glyph--warning">
        <AlertTriangle className="w-5 h-5" />
      </div>
    );

  const heartbeat = lastResult?.heartbeat ?? null;
  const cacheNodes = lastResult?.cacheNodes ?? [];
  const totalCacheNodeIps = cacheNodes.reduce((sum, node) => sum + node.ips.length, 0);
  const avgLatencyMs = lastResult?.avgLatencyMs ?? null;

  // The verdict sentence already gives the tally, so the breakdown names only the services that
  // need a look, one row per kind of trouble, plus the services the check could not judge.
  const breakdownRows: VerdictBreakdownRow[] = isRunning
    ? []
    : (
        [
          {
            id: 'mismatched',
            tone: 'warning',
            services: mismatchedServices,
            label: t(`${keys}.breakdownMismatched`, { count: mismatchedServices.length })
          },
          {
            id: 'unresolved',
            tone: 'error',
            services: unresolvedOnlyServices,
            label: t(`${keys}.breakdownUnresolved`, { count: unresolvedOnlyServices.length })
          },
          {
            id: 'partial',
            tone: 'warning',
            services: partialOnlyServices,
            label: t(`${keys}.breakdownPartial`, { count: partialOnlyServices.length })
          },
          {
            id: 'unverified',
            tone: 'info',
            services:
              lastResult?.services.filter((service) => service.status === 'unverified') ?? [],
            label: t(`${keys}.breakdownUnverified`, { count: summary?.unverifiedServices ?? 0 })
          },
          {
            id: 'disabled',
            tone: 'idle',
            services: lastResult?.services.filter((service) => service.status === 'disabled') ?? [],
            label: t(`${keys}.breakdownDisabled`, { count: summary?.disabledServices ?? 0 })
          }
        ] satisfies VerdictBreakdownRow[]
      ).filter((row) => row.services.length > 0);

  const expectedCacheIps = lastResult?.expectedCacheIps ?? [];
  const expectedIpSentence =
    lastResult && expectedCacheIps.length > 0
      ? t(`${keys}.expectedIp`, {
          ips: expectedCacheIps.join(', '),
          source: t(`${keys}.expectedIpSource.${lastResult.expectedIpSource}`)
        })
      : null;

  const showFacts = !isRunning && !!lastResult;

  // How the run went, in one line under the verdict: when it ran and who answered as the cache.
  // Node evidence first, then the locator heartbeat; with neither, the expected cache IP stays on
  // screen beside the heartbeat failure line, so the reader still sees which address was tried.
  const checkedSentence = !showFacts
    ? null
    : cacheNodes.length > 0
      ? totalCacheNodeIps > 1
        ? t(`${keys}.cacheAnsweredMore`, {
            time: lastCheckedTime,
            ip: cacheNodes[0].ips[0],
            host: cacheNodes[0].servedBy,
            count: totalCacheNodeIps - 1
          })
        : t(`${keys}.heartbeatOk`, {
            time: lastCheckedTime,
            ip: cacheNodes[0].ips[0],
            host: cacheNodes[0].servedBy
          })
      : heartbeat?.reachable
        ? heartbeat.servedBy
          ? t(`${keys}.heartbeatOk`, {
              time: lastCheckedTime,
              ip: heartbeat.cacheIp,
              host: heartbeat.servedBy
            })
          : t(`${keys}.heartbeatOkNoHost`, { time: lastCheckedTime, ip: heartbeat.cacheIp })
        : expectedCacheIps.length > 0
          ? t(`${keys}.checkedExpected`, {
              time: lastCheckedTime,
              ips: expectedCacheIps.join(', ')
            })
          : t(`${keys}.checkedOnly`, { time: lastCheckedTime });
  // Which resolver the run asked. Only the detected container is called lancache-dns; a configured
  // server can be any DNS server.
  const resolverLine = !showFacts
    ? null
    : lastResult.resolverSource === 'configured'
      ? t(`${keys}.resolverConfigured`, { server: lastResult.dnsServer })
      : lastResult.resolverSource === 'detected'
        ? t(`${keys}.resolverDetected`, { server: lastResult.dnsServer })
        : t(`${keys}.resolverSystem`);
  // The detail behind the line: the resolver, what was expected and from where, every node, and
  // the timings.
  const factsLines = [
    resolverLine,
    expectedIpSentence,
    cacheNodes.length > 0
      ? cacheNodes.map((node) => `${node.servedBy} (${node.ips.join(', ')})`).join(' · ')
      : null,
    avgLatencyMs !== null ? t(`${keys}.metaAvgLookup`, { ms: avgLatencyMs }) : null,
    verifiedDomains > 0 ? t(`${keys}.metaLiveAnswers`, { count: verifiedDomains }) : null
  ].filter((line): line is string => line !== null);
  // One line each, so the node list never runs into the timing sentence after it.
  const factsTooltip =
    factsLines.length > 0
      ? factsLines.map((line) => (
          <span key={line} className="block">
            {line}
          </span>
        ))
      : null;

  return (
    <Card>
      <div className="status-check-verdict-header">
        <div className="status-check-verdict-lead">
          {glyph}
          <div className="status-check-verdict-copy">
            <div className="status-check-verdict-title status-check-help-line">
              <h3 className="status-check-verdict-line">{verdictLine}</h3>
              <HelpPopover width={320} aria-label={t(`${keys}.scopeDescriptionLabel`)}>
                {t(`${keys}.scopeDescription`)}
              </HelpPopover>
            </div>
            {isRunning && progress?.currentService ? (
              <p className="status-check-verdict-sub">
                {t(`${keys}.sweepCurrentService`, {
                  service: formatServiceLabel(progress.currentService)
                })}
              </p>
            ) : (
              checkedSentence && (
                <Tooltip content={factsTooltip} className="block min-w-0" position="bottom">
                  <p className="status-check-verdict-sub">{checkedSentence}</p>
                </Tooltip>
              )
            )}
            {!isRunning && cantVerify && (
              <p className="status-check-verdict-hint">{t(`${keys}.verdictUnverifiedHint`)}</p>
            )}
            {!isRunning &&
              lastResult?.resolverSource === 'system' &&
              failingServices.length > 0 && (
                <p className="status-check-verdict-warning status-check-help-line">
                  {t(`${keys}.resolverSystemHint`)}
                  <HelpPopover width={320} aria-label={t(`${keys}.systemResolverCaveatLabel`)}>
                    {t(`${keys}.systemResolverCaveat`)}
                  </HelpPopover>
                </p>
              )}
          </div>
        </div>
        <div className="status-check-verdict-actions">
          {resolverChoice}
          <Button variant="filled" color="run" size="md" loading={isRunning} onClick={onRun}>
            {t(`${keys}.runCheck`)}
          </Button>
        </div>
      </div>

      {breakdownRows.length > 0 && (
        <div className="status-check-breakdown">
          {breakdownRows.map((row) => {
            const labels = row.services.map((service) => formatServiceLabel(service.service));
            const { shown, moreCount } = splitExamples(labels, CHIP_LIMIT);
            return (
              <div key={row.id} className="status-check-breakdown-row cluster">
                <span className={`status-dot ${row.tone}`} aria-hidden="true" />
                <span className="status-check-breakdown-count tabular-nums">{row.label}</span>
                <span className="status-check-chips">
                  {shown.map((label) => (
                    <span key={label} className="status-check-chip">
                      {label}
                    </span>
                  ))}
                  {moreCount > 0 && (
                    <span className="status-check-chip status-check-chip--more">
                      {t(`${keys}.breakdownMore`, { count: moreCount })}
                    </span>
                  )}
                </span>
              </div>
            );
          })}
        </div>
      )}

      {ribbonSegments.length > 0 && (
        <ResolutionRibbon
          segments={ribbonSegments}
          interactive={ribbonInteractive}
          onSegmentClick={onRibbonSegmentClick}
        />
      )}

      {showFacts && heartbeat && !heartbeat.reachable && cacheNodes.length === 0 && (
        <p className="text-xs text-themed-warning mt-2">
          {t(`${keys}.heartbeatFailed`, {
            error: translateStageKeyMessage(heartbeat.error, undefined, `${keys}.unknownError`)
          })}
        </p>
      )}

      <ContentPathSummary report={lastResult?.contentReport} isRunning={isRunning} />

      {runErrorText && (
        <Alert color={runErrorColor} className="mt-3">
          {runErrorText}
        </Alert>
      )}
    </Card>
  );
};

export default VerdictCard;
