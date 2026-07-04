import React from 'react';
import { useTranslation } from 'react-i18next';
import { Activity, AlertTriangle, CheckCircle2, XCircle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { formatDateTime } from '@utils/formatters';
import type { StatusCheckResult, StatusCheckSummary } from '@services/api.service';
import ResolutionRibbon from './ResolutionRibbon';
import { formatServiceLabel, formatServiceList } from './helpers';
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
  const { t, i18n } = useTranslation();
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
  const totalPartialFailing = partialOnlyServices.reduce(
    (sum, service) => sum + (service.totalCount - service.resolvedCount),
    0
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
        }) + (avgLatencyMs !== null ? ` · ${t(`${keys}.avgLatency`, { ms: avgLatencyMs })}` : '')
      : null;

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
            {!isRunning && mismatchedServices.length > 0 && (
              <p className="text-sm text-themed-secondary">
                {t(`${keys}.problemsMismatched`, {
                  services: formatServiceList(
                    mismatchedServices.map((service) => service.service),
                    i18n.language
                  )
                })}
              </p>
            )}
            {!isRunning && unresolvedOnlyServices.length > 0 && (
              <p className="text-sm text-themed-secondary">
                {t(`${keys}.problemsUnresolved`, {
                  services: formatServiceList(
                    unresolvedOnlyServices.map((service) => service.service),
                    i18n.language
                  )
                })}
              </p>
            )}
            {!isRunning && partialOnlyServices.length === 1 && (
              <p className="text-sm text-themed-secondary tabular-nums">
                {t(`${keys}.problemsPartialSingle`, {
                  service: formatServiceLabel(partialOnlyServices[0].service),
                  failing: partialOnlyServices[0].totalCount - partialOnlyServices[0].resolvedCount,
                  count: partialOnlyServices[0].totalCount
                })}
              </p>
            )}
            {!isRunning && partialOnlyServices.length > 1 && (
              <p className="text-sm text-themed-secondary tabular-nums">
                {t(`${keys}.problemsPartialMulti`, {
                  services: formatServiceList(
                    partialOnlyServices.map((service) => service.service),
                    i18n.language
                  ),
                  count: totalPartialFailing
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

      {ribbonSegments.length > 0 && (
        <ResolutionRibbon
          segments={ribbonSegments}
          interactive={ribbonInteractive}
          onSegmentClick={onRibbonSegmentClick}
        />
      )}

      {!isRunning && summary && (
        <p className="text-xs text-themed-muted tabular-nums">
          {t(`${keys}.ribbonSummary`, {
            resolved: summary.resolvedServices,
            partial: summary.partialServices,
            unresolved: summary.unresolvedServices
          })}
          {summary.unverifiedServices > 0
            ? ` · ${t(`${keys}.ribbonSummaryUnverified`, { unverified: summary.unverifiedServices })}`
            : ''}
          {summary.disabledServices > 0
            ? ` · ${t(`${keys}.ribbonSummaryDisabled`, { disabled: summary.disabledServices })}`
            : ''}
        </p>
      )}

      {!isRunning && lastResult && lastResult.expectedCacheIps.length > 0 && (
        <p className="text-xs text-themed-muted mt-2 tabular-nums">
          {t(`${keys}.expectedIp`, {
            ips: lastResult.expectedCacheIps.join(', '),
            source: t(`${keys}.expectedIpSource.${lastResult.expectedIpSource}`)
          })}
        </p>
      )}

      {!isRunning && heartbeat && (
        <p className="text-xs text-themed-muted mt-2 tabular-nums">
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

      {!isRunning && lastResult && (
        <p className="text-xs text-themed-muted">
          {lastResult.resolverSource === 'configured'
            ? t(`${keys}.resolverConfigured`, { server: lastResult.dnsServer })
            : lastResult.resolverSource === 'detected'
              ? t(`${keys}.resolverDetected`, { server: lastResult.dnsServer })
              : t(`${keys}.resolverSystem`)}
        </p>
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
