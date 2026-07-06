import React from 'react';
import { useTranslation } from 'react-i18next';
import { Activity, AlertTriangle, CheckCircle2, XCircle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import { formatDateTime } from '@utils/formatters';
import type {
  StatusCheckResult,
  StatusCheckServiceResult,
  StatusCheckSummary
} from '@services/api.service';
import ResolutionRibbon from './ResolutionRibbon';
import { formatServiceLabel, splitExamples } from './helpers';
import type { RibbonSegment, StatusCheckProgressEvent } from './types';

interface VerdictCardProps {
  lastResult: StatusCheckResult | null;
  isRunning: boolean;
  progress: StatusCheckProgressEvent | null;
  runError: string | null;
  ribbonSegments: RibbonSegment[];
  ribbonInteractive: boolean;
  onRibbonSegmentClick: (service: string) => void;
  onRun: () => void;
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
  onRun
}) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';

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
        <div className="flex flex-col items-center text-center gap-3 py-6">
          <div className="status-check-glyph status-check-glyph--neutral">
            <Activity className="w-5 h-5" />
          </div>
          <p className="font-medium text-themed-primary">{t(`${keys}.emptyTitle`)}</p>
          <p className="text-sm text-themed-secondary max-w-xl">{t(`${keys}.emptyBody`)}</p>
          <Button variant="filled" color="blue" size="md" onClick={onRun}>
            {t(`${keys}.runCheck`)}
          </Button>
          {runError && (
            <Alert color="red" className="w-full text-left">
              {t(`${keys}.sweepFailed`, { error: runError })}
            </Alert>
          )}
        </div>
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

  // v1.4: domain-level heartbeat probes can verify cache nodes even when the locator-level
  // heartbeat (a single candidate IP) failed to answer - never show both a "did not answer" line
  // and evidence that it plainly did. The nodes+avg line replaces the error line in that case.
  const cacheNodesLine =
    cacheNodes.length > 0
      ? t(`${keys}.cacheNodesLine`, {
          count: totalCacheNodeIps,
          ips: totalCacheNodeIps,
          nodes:
            cacheNodes.length === 1
              ? t(`${keys}.cacheNodesSingle`, { host: cacheNodes[0].servedBy })
              : t(`${keys}.cacheNodesMulti`, { count: cacheNodes.length })
        })
      : null;

  // Primary at-a-glance signal: the verdict tally as prominent count pills. Zero buckets stay
  // muted so the ones that actually hold services carry the eye.
  const statPills: { id: string; value: number; label: string }[] = summary
    ? [
        { id: 'resolved', value: summary.resolvedServices, label: t(`${keys}.statLabel.resolved`) },
        { id: 'partial', value: summary.partialServices, label: t(`${keys}.statLabel.partial`) },
        {
          id: 'unresolved',
          value: summary.unresolvedServices,
          label: t(`${keys}.statLabel.unresolved`)
        },
        ...(summary.unverifiedServices > 0
          ? [
              {
                id: 'unverified',
                value: summary.unverifiedServices,
                label: t(`${keys}.statLabel.unverified`)
              }
            ]
          : []),
        ...(summary.disabledServices > 0
          ? [
              {
                id: 'disabled',
                value: summary.disabledServices,
                label: t(`${keys}.statLabel.disabled`)
              }
            ]
          : [])
      ]
    : [];

  // Failure breakdown: one row per failure type with a count and a few example chips, replacing
  // the old comma-run sentences that spelled out every failing service name.
  const failureBuckets: {
    id: string;
    dot: string;
    services: StatusCheckServiceResult[];
    label: string;
  }[] = [
    {
      id: 'mismatched',
      dot: 'mismatched',
      services: mismatchedServices,
      label: t(`${keys}.breakdownMismatched`, { count: mismatchedServices.length })
    },
    {
      id: 'unresolved',
      dot: 'unresolved',
      services: unresolvedOnlyServices,
      label: t(`${keys}.breakdownUnresolved`, { count: unresolvedOnlyServices.length })
    },
    {
      id: 'partial',
      dot: 'partial',
      services: partialOnlyServices,
      label: t(`${keys}.breakdownPartial`, { count: partialOnlyServices.length })
    }
  ].filter((bucket) => bucket.services.length > 0);

  // Collapse the expected-cache-IP list to one IP + "+N more" (full list in the title tooltip).
  const expectedCacheIps = lastResult?.expectedCacheIps ?? [];
  const { shown: expectedIpShown, moreCount: expectedIpMore } = splitExamples(expectedCacheIps, 1);
  const expectedIpLabel =
    expectedIpShown[0] +
    (expectedIpMore > 0 ? ` ${t(`${keys}.ipMore`, { count: expectedIpMore })}` : '');

  const showMeta = !isRunning && !!lastResult;

  return (
    <Card>
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="flex items-start gap-3 min-w-0">
          {glyph}
          <div className="min-w-0">
            <p className="font-medium text-themed-primary tabular-nums">{verdictLine}</p>
            {!isRunning && cantVerify && (
              <p className="text-sm text-themed-secondary">{t(`${keys}.verdictUnverifiedHint`)}</p>
            )}
            {!isRunning &&
              lastResult?.resolverSource === 'system' &&
              failingServices.length > 0 && (
                <p className="text-sm text-[var(--theme-warning)]">
                  {t(`${keys}.systemResolverCaveat`)}
                </p>
              )}
            {!isRunning && verifiedDomains > 0 && (
              <p className="text-sm text-themed-secondary">
                {t(`${keys}.verifiedLive`, { count: verifiedDomains })}
              </p>
            )}
            {isRunning && progress?.currentService && (
              <p className="text-sm text-themed-muted">
                {t(`${keys}.sweepCurrentService`, {
                  service: formatServiceLabel(progress.currentService)
                })}
              </p>
            )}
          </div>
        </div>
        <div className="flex flex-col items-start sm:items-end gap-1 flex-shrink-0">
          <Button variant="filled" color="blue" size="md" loading={isRunning} onClick={onRun}>
            {t(`${keys}.runCheck`)}
          </Button>
          {lastResult && (
            <span className="text-xs text-themed-muted">
              {t(`${keys}.lastChecked`, { time: formatDateTime(lastResult.completedAtUtc) })}
            </span>
          )}
        </div>
      </div>

      {!isRunning && summary && (
        <div className="status-check-stats mt-3">
          {statPills.map((pill) => (
            <div
              key={pill.id}
              className={`status-check-stat status-check-stat--${pill.id}${
                pill.value === 0 ? ' status-check-stat--zero' : ''
              }`}
            >
              <span className="status-check-stat-value tabular-nums">{pill.value}</span>
              <span className="status-check-stat-label">{pill.label}</span>
            </div>
          ))}
        </div>
      )}

      {!isRunning && failureBuckets.length > 0 && (
        <div className="status-check-breakdown mt-3">
          {failureBuckets.map((bucket) => {
            const labels = bucket.services.map((service) => formatServiceLabel(service.service));
            const { shown, moreCount } = splitExamples(labels, CHIP_LIMIT);
            return (
              <div key={bucket.id} className="status-check-breakdown-row">
                <span
                  className={`status-check-breakdown-dot status-check-breakdown-dot--${bucket.dot}`}
                />
                <span className="status-check-breakdown-count tabular-nums">{bucket.label}</span>
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

      {showMeta && (
        <div className="status-check-meta">
          {expectedCacheIps.length > 0 && (
            <Tooltip
              content={expectedCacheIps.join(', ')}
              className="text-xs text-themed-muted tabular-nums"
            >
              {t(`${keys}.expectedIp`, {
                ips: expectedIpLabel,
                source: t(`${keys}.expectedIpSource.${lastResult.expectedIpSource}`)
              })}
            </Tooltip>
          )}
          {heartbeat && (
            <p className="text-xs text-themed-muted tabular-nums">
              {heartbeat.reachable
                ? heartbeat.servedBy
                  ? t(`${keys}.heartbeatOk`, { ip: heartbeat.cacheIp, host: heartbeat.servedBy })
                  : t(`${keys}.heartbeatOkNoHost`, { ip: heartbeat.cacheIp })
                : cacheNodesLine
                  ? cacheNodesLine
                  : t(`${keys}.heartbeatFailed`, {
                      error: heartbeat.error ?? t(`${keys}.unknownError`)
                    })}
            </p>
          )}
          {avgLatencyMs !== null && (
            <p className="text-xs text-themed-muted tabular-nums">
              {t(`${keys}.avgLatency`, { ms: avgLatencyMs })}
            </p>
          )}
          <p className="text-xs text-themed-muted">
            {lastResult.resolverSource === 'configured'
              ? t(`${keys}.resolverConfigured`, { server: lastResult.dnsServer })
              : lastResult.resolverSource === 'detected'
                ? t(`${keys}.resolverDetected`, { server: lastResult.dnsServer })
                : t(`${keys}.resolverSystem`)}
          </p>
        </div>
      )}

      {runError && (
        <Alert color="red" className="mt-3">
          {t(`${keys}.sweepFailed`, { error: runError })}
        </Alert>
      )}
    </Card>
  );
};

export default VerdictCard;
