import React from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, Ban, CheckCircle2, HelpCircle, Minus, XCircle } from 'lucide-react';
import { Tooltip } from '@components/ui/Tooltip';
import type { StatusCheckDomainResult } from '@services/api.service';
import {
  getProtocolOutcomeTranslationKey,
  getProtocolReasonTranslationKey
} from './contentPathHelpers';

interface DomainLeafRowProps {
  result: StatusCheckDomainResult;
}

type CheckState = 'pass' | 'warn' | 'fail' | 'info' | 'blocked' | 'neutral';

/** One labeled per-domain check line (DNS, heartbeat, public-edge HTTP/HTTPS). */
interface DomainCheckLine {
  id: string;
  state: CheckState;
  label: string;
  detail: string;
  tooltip?: string;
}

const CheckIcon: React.FC<{ state: CheckState }> = ({ state }) =>
  state === 'pass' ? (
    <CheckCircle2 className="status-check-check-icon text-[var(--theme-success)]" />
  ) : state === 'warn' ? (
    <AlertTriangle className="status-check-check-icon text-[var(--theme-warning)]" />
  ) : state === 'fail' ? (
    <XCircle className="status-check-check-icon text-[var(--theme-error)]" />
  ) : state === 'info' ? (
    <HelpCircle className="status-check-check-icon text-[var(--theme-info)]" />
  ) : state === 'blocked' ? (
    <Ban className="status-check-check-icon text-[var(--theme-text-muted)]" />
  ) : (
    <Minus className="status-check-check-icon text-[var(--theme-text-muted)]" />
  );

// One block per domain: the entry name plus one line per independent check - what DNS answered,
// whether anything answered the lancache heartbeat, and how the host's real public edges behave
// over HTTP/HTTPS - each with its own pass/fail glyph so no verdict hides inside another.
const DomainLeafRow: React.FC<DomainLeafRowProps> = ({ result }) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';
  const contentKeys = `${keys}.content`;
  const isWildcardProbe = result.domain !== result.originalEntry;
  const primaryIp = result.resolvedIps[0];
  const moreIps =
    result.resolvedIps.length > 1
      ? ` ${t(`${keys}.ipMore`, { count: result.resolvedIps.length - 1 })}`
      : '';
  const latency = result.latencyMs !== null ? ` · ${result.latencyMs} ms` : '';

  const lines: DomainCheckLine[] = [];

  const dnsLabel = t(`${keys}.checks.dns`);
  if (result.status === 'unresolved') {
    lines.push({
      id: 'dns',
      state: 'fail',
      label: dnsLabel,
      detail: result.error ?? t(`${keys}.unknownError`)
    });
  } else if (result.status === 'blocked') {
    lines.push({
      id: 'dns',
      state: 'blocked',
      label: dnsLabel,
      detail: t(`${keys}.checkDnsBlocked`),
      tooltip: t(`${keys}.reasonBlocked`)
    });
  } else if (result.status === 'mismatched') {
    lines.push({
      id: 'dns',
      state: 'fail',
      label: dnsLabel,
      detail: `${t(`${keys}.checkDnsWrongIp`, { ip: primaryIp })}${moreIps}${latency}`
    });
  } else if (result.status === 'unverified') {
    lines.push({
      id: 'dns',
      state: 'info',
      label: dnsLabel,
      detail: `${primaryIp}${moreIps}${latency} · ${t(`${keys}.tagUnverified`)}`
    });
  } else {
    lines.push({
      id: 'dns',
      state: 'pass',
      label: dnsLabel,
      detail: `${primaryIp}${moreIps}${latency}`
    });
  }

  const heartbeatLabel = t(`${keys}.checks.heartbeat`);
  if (result.heartbeatVerified) {
    lines.push({
      id: 'heartbeat',
      state: 'pass',
      label: heartbeatLabel,
      detail: result.servedBy
        ? t(`${keys}.checkHeartbeatVerified`, { host: result.servedBy })
        : t(`${keys}.checkHeartbeatVerifiedNoHost`)
    });
  } else if (result.status === 'resolved') {
    lines.push({
      id: 'heartbeat',
      state: 'warn',
      label: heartbeatLabel,
      detail: t(`${keys}.checkHeartbeatExpectedMatch`)
    });
  } else if (result.status === 'unverified') {
    lines.push({
      id: 'heartbeat',
      state: 'fail',
      label: heartbeatLabel,
      detail: t(`${keys}.checkHeartbeatNone`)
    });
  } else {
    lines.push({
      id: 'heartbeat',
      state: 'neutral',
      label: heartbeatLabel,
      detail: t(`${keys}.checkHeartbeatNotAttempted`)
    });
  }

  // The origin line answers the user's real question - "can this domain still be cached?" -
  // instead of protocol trivia. Lancache only caches plain HTTP, so an origin that has gone
  // HTTPS-only means downloads bypass the cache no matter what DNS does. Protocol-level detail
  // stays in the tooltip (and the Test-a-domain drill-down).
  const edgeLabel = t(`${keys}.checks.edge`);
  const edgeProbe = result.edgeProbe ?? null;
  if (!edgeProbe) {
    lines.push({
      id: 'edge',
      state: 'neutral',
      label: edgeLabel,
      detail: t(`${keys}.checkEdgeNotTested`)
    });
  } else {
    const status = edgeProbe.protocolStatus;
    const consensusText =
      edgeProbe.consensusEdges > 0 && edgeProbe.totalPublicEdges > 0
        ? ` · ${t(`${keys}.checkEdgeConsensus`, {
            consensus: edgeProbe.consensusEdges,
            total: edgeProbe.totalPublicEdges
          })}`
        : '';
    let state: CheckState = 'neutral';
    let detail: string;
    if (status === 'bothUsable' || status === 'httpUsable') {
      state = 'pass';
      detail = `${t(`${keys}.checkOriginHttp`)}${consensusText}`;
    } else if (status === 'httpsOnlyCandidate') {
      state = 'warn';
      detail = `${t(`${keys}.checkOriginHttpsOnly`)}${consensusText}`;
    } else if (status === 'notRun' && edgeProbe.protocolReason === 'wildcardEntry') {
      detail = t(`${contentKeys}.protocolReason.wildcardEntry`);
    } else if (edgeProbe.protocolReason === 'noPublicEdges') {
      detail = t(`${keys}.checkOriginNotPublic`);
    } else if (edgeProbe.protocolReason === 'nonDefinitiveEdges') {
      detail = t(`${keys}.checkOriginNoAnswer`);
    } else {
      detail = t(`${keys}.checkOriginUnknown`);
    }

    const inconclusiveLike = status === 'inconclusive' || status === 'notRun';
    const reasonText =
      inconclusiveLike && edgeProbe.protocolReason
        ? ` ${t(getProtocolReasonTranslationKey(edgeProbe.protocolReason))}`
        : '';
    const reachableEdge =
      edgeProbe.edges.find(
        (edge) => edge.http.outcome !== 'connectFailure' || edge.https.outcome !== 'connectFailure'
      ) ?? edgeProbe.edges[0];
    const outcomesSuffix =
      edgeProbe.protocolReason === 'nonDefinitiveEdges' && reachableEdge
        ? ` HTTP ${t(getProtocolOutcomeTranslationKey(reachableEdge.http.outcome))} · HTTPS ${t(
            getProtocolOutcomeTranslationKey(reachableEdge.https.outcome)
          )}.`
        : '';
    lines.push({
      id: 'edge',
      state,
      label: edgeLabel,
      detail,
      tooltip: `${t(`${contentKeys}.protocolDetail.${status}`)}${reasonText}${outcomesSuffix}`
    });
  }

  const tag =
    result.status === 'mismatched' ? (
      <span className="status-check-tag status-check-tag--wrong">{t(`${keys}.tagWrongIp`)}</span>
    ) : result.status === 'unverified' ? (
      <span className="status-check-tag status-check-tag--unverified">
        {t(`${keys}.tagUnverified`)}
      </span>
    ) : result.status === 'blocked' ? (
      <span className="status-check-tag status-check-tag--blocked">{t(`${keys}.tagBlocked`)}</span>
    ) : result.status === 'unresolved' ? (
      <span className="status-check-tag status-check-tag--none">{t(`${keys}.tagNoAnswer`)}</span>
    ) : null;

  const domainName = <span className="status-check-domain-name">{result.originalEntry}</span>;

  return (
    <div className="status-check-domain">
      <div className="status-check-domain-head">
        {isWildcardProbe ? (
          <Tooltip content={t(`${keys}.testedAs`, { domain: result.domain })} className="min-w-0">
            {domainName}
          </Tooltip>
        ) : (
          domainName
        )}
        {tag}
      </div>
      <div className="status-check-checks">
        {lines.map((line) => {
          const content = (
            <>
              <CheckIcon state={line.state} />
              <span className="status-check-check-label caps-label caps-label--sm">
                {line.label}
              </span>
              <span className="status-check-check-detail tabular-nums">{line.detail}</span>
            </>
          );
          return line.tooltip ? (
            <Tooltip key={line.id} content={line.tooltip} className="status-check-check">
              {content}
            </Tooltip>
          ) : (
            <div key={line.id} className="status-check-check">
              {content}
            </div>
          );
        })}
      </div>
    </div>
  );
};

export default DomainLeafRow;
