import React from 'react';
import '../managementSectionContent.css';
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
import ContentPathSummary from './ContentPathSummary';
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
  /** The resolver-mode picker, rendered as a quiet toolbar at the top of the card. */
  resolverControl?: React.ReactNode;
}

/** One labeled tile in the verdict readout grid. */
interface VerdictStatTile {
  id: string;
  value: string;
  label: string;
  tone: 'success' | 'warning' | 'error' | 'info' | null;
  isZero: boolean;
}

/** One labeled slot in the meta strip under the ribbon. */
interface VerdictMetaSlot {
  id: string;
  value: string;
  label: string;
  tooltip: string | null;
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
        {resolverControl && <div className="status-check-resolver">{resolverControl}</div>}
        <div className="flex flex-col items-center text-center gap-3 py-6">
          <div className="status-check-glyph status-check-glyph--neutral">
            <Activity className="w-5 h-5" />
          </div>
          <h3 className="font-medium text-themed-primary">{t(`${keys}.emptyTitle`)}</h3>
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

  // Primary at-a-glance signal: the verdict tally plus the key live figures as one labeled
  // readout grid. The former standalone sentences (live heartbeat answers, average DNS
  // latency) become tiles here instead of loose paragraphs.
  const statTiles: VerdictStatTile[] = [];
  if (!isRunning && summary) {
    statTiles.push(
      {
        id: 'resolved',
        value: String(summary.resolvedServices),
        label: t(`${keys}.statLabel.resolved`),
        tone: 'success',
        isZero: summary.resolvedServices === 0
      },
      {
        id: 'partial',
        value: String(summary.partialServices),
        label: t(`${keys}.statLabel.partial`),
        tone: 'warning',
        isZero: summary.partialServices === 0
      },
      {
        id: 'unresolved',
        value: String(summary.unresolvedServices),
        label: t(`${keys}.statLabel.unresolved`),
        tone: 'error',
        isZero: summary.unresolvedServices === 0
      }
    );
    if (summary.unverifiedServices > 0) {
      statTiles.push({
        id: 'unverified',
        value: String(summary.unverifiedServices),
        label: t(`${keys}.statLabel.unverified`),
        tone: 'info',
        isZero: false
      });
    }
    if (summary.disabledServices > 0) {
      statTiles.push({
        id: 'disabled',
        value: String(summary.disabledServices),
        label: t(`${keys}.statLabel.disabled`),
        tone: null,
        isZero: true
      });
    }
    if (verifiedDomains > 0) {
      statTiles.push({
        id: 'liveAnswers',
        value: String(verifiedDomains),
        label: t(`${keys}.readoutLiveAnswers`),
        tone: 'success',
        isZero: false
      });
    }
    if (avgLatencyMs !== null) {
      statTiles.push({
        id: 'avgDns',
        value: t(`${keys}.readoutAvgDnsValue`, { ms: avgLatencyMs }),
        label: t(`${keys}.readoutAvgDns`),
        tone: null,
        isZero: false
      });
    }
  }

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

  // The labeled meta strip: expected cache IP, answering cache nodes (or the locator
  // heartbeat when no node evidence exists), and the resolver the sweep used. Full
  // sentences live in each slot's tooltip; a failed heartbeat with no node evidence
  // stays a visible warning line below the strip.
  const metaSlots: VerdictMetaSlot[] = [];
  if (showMeta && lastResult) {
    if (expectedCacheIps.length > 0) {
      metaSlots.push({
        id: 'expectedIp',
        value: expectedIpLabel,
        label: t(`${keys}.metaExpectedIp`),
        tooltip: t(`${keys}.expectedIp`, {
          ips: expectedCacheIps.join(', '),
          source: t(`${keys}.expectedIpSource.${lastResult.expectedIpSource}`)
        })
      });
    }
    if (cacheNodes.length > 0) {
      metaSlots.push({
        id: 'cacheNodes',
        value:
          cacheNodes.length === 1
            ? t(`${keys}.metaCacheNodesSingle`, {
                host: cacheNodes[0].servedBy,
                count: totalCacheNodeIps
              })
            : t(`${keys}.metaCacheNodesMulti`, {
                nodes: cacheNodes.length,
                count: totalCacheNodeIps
              }),
        label: t(`${keys}.metaCacheNodes`),
        tooltip: cacheNodes.map((node) => `${node.servedBy} (${node.ips.join(', ')})`).join(' · ')
      });
    } else if (heartbeat?.reachable) {
      metaSlots.push({
        id: 'heartbeat',
        value: heartbeat.servedBy ?? heartbeat.cacheIp ?? '',
        label: t(`${keys}.metaHeartbeat`),
        tooltip: heartbeat.servedBy
          ? t(`${keys}.heartbeatOk`, { ip: heartbeat.cacheIp, host: heartbeat.servedBy })
          : t(`${keys}.heartbeatOkNoHost`, { ip: heartbeat.cacheIp })
      });
    }
    metaSlots.push({
      id: 'resolver',
      value:
        lastResult.resolverSource === 'system'
          ? t(`${keys}.metaResolverSystemValue`)
          : (lastResult.dnsServer ?? ''),
      label:
        lastResult.resolverSource === 'configured'
          ? t(`${keys}.metaResolverConfigured`)
          : lastResult.resolverSource === 'detected'
            ? t(`${keys}.metaResolverDetected`)
            : t(`${keys}.metaResolverSystem`),
      tooltip:
        lastResult.resolverSource === 'configured'
          ? t(`${keys}.resolverConfigured`, { server: lastResult.dnsServer })
          : lastResult.resolverSource === 'detected'
            ? t(`${keys}.resolverDetected`, { server: lastResult.dnsServer })
            : t(`${keys}.resolverSystem`)
    });
  }

  return (
    <Card>
      {resolverControl && <div className="status-check-resolver">{resolverControl}</div>}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="flex items-start gap-3 min-w-0">
          {glyph}
          <div className="min-w-0">
            <h3 className="status-check-scope-title">{t(`${keys}.scopeTitle`)}</h3>
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

      {statTiles.length > 0 && (
        <div className="mgmt-stat-grid mt-4">
          {statTiles.map((tile) => (
            <div key={tile.id} className="mgmt-stat">
              <p className="mgmt-stat__label caps-label caps-label--sm">{tile.label}</p>
              <p
                className={`mgmt-stat__value tabular-nums${
                  tile.isZero
                    ? ' status-check-value--zero'
                    : tile.tone
                      ? ` status-check-value--${tile.tone}`
                      : ''
                }`}
              >
                {tile.value}
              </p>
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

      {metaSlots.length > 0 && (
        <div className="status-check-meta">
          {metaSlots.map((slot) =>
            slot.tooltip ? (
              <Tooltip key={slot.id} content={slot.tooltip} className="status-check-meta-item">
                <span className="status-check-meta-value">{slot.value}</span>
                <span className="status-check-meta-label caps-label caps-label--sm">
                  {slot.label}
                </span>
              </Tooltip>
            ) : (
              <div key={slot.id} className="status-check-meta-item">
                <span className="status-check-meta-value">{slot.value}</span>
                <span className="status-check-meta-label caps-label caps-label--sm">
                  {slot.label}
                </span>
              </div>
            )
          )}
        </div>
      )}
      {showMeta && heartbeat && !heartbeat.reachable && cacheNodes.length === 0 && (
        <p className="text-xs text-[var(--theme-warning)] mt-2">
          {t(`${keys}.heartbeatFailed`, { error: heartbeat.error ?? t(`${keys}.unknownError`) })}
        </p>
      )}

      <ContentPathSummary report={lastResult?.contentReport} isRunning={isRunning} />

      {runError && (
        <Alert color="red" className="mt-3">
          {t(`${keys}.sweepFailed`, { error: runError })}
        </Alert>
      )}
    </Card>
  );
};

export default VerdictCard;
