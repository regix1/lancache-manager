import React from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, CheckCircle2, HelpCircle, XCircle } from 'lucide-react';
import type { StatusCheckDomainResult } from '@services/api.service';

interface DomainLeafRowProps {
  result: StatusCheckDomainResult;
}

// One line per domain: an icon + the entry on the left, and what it resolved to plus a
// compact status tag on the right. The expected cache IPs are shown ONCE per service
// (in ServiceResultsList), never reprinted here, so each row stays scannable.
const DomainLeafRow: React.FC<DomainLeafRowProps> = ({ result }) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';
  const isWildcardProbe = result.domain !== result.originalEntry;
  const testedAsNote = t(`${keys}.testedAs`, { domain: result.domain });
  const primaryIp = result.resolvedIps[0];

  const resolvedInfo = primaryIp ? (
    <span className="text-xs text-themed-muted tabular-nums">
      {primaryIp}
      {result.resolvedIps.length > 1 ? ` +${result.resolvedIps.length - 1}` : ''}
      {result.latencyMs !== null ? ` · ${result.latencyMs} ms` : ''}
      {result.heartbeatVerified && result.servedBy
        ? ` · ${t(`${keys}.rowServedBy`, { host: result.servedBy })}`
        : ''}
    </span>
  ) : null;

  const tag =
    result.status === 'mismatched' ? (
      <span className="status-check-tag status-check-tag--wrong">{t(`${keys}.tagWrongIp`)}</span>
    ) : result.status === 'unverified' ? (
      <span className="status-check-tag status-check-tag--unverified">
        {t(`${keys}.tagUnverified`)}
      </span>
    ) : result.status === 'unresolved' ? (
      <span className="status-check-tag status-check-tag--none">{t(`${keys}.tagNoAnswer`)}</span>
    ) : null;

  const rowTitle =
    result.status === 'unresolved'
      ? t(`${keys}.reasonUnresolved`, { error: result.error ?? t(`${keys}.unknownError`) })
      : isWildcardProbe
        ? testedAsNote
        : undefined;

  return (
    <div className="flex items-center justify-between gap-2 flex-wrap" title={rowTitle}>
      <div className="flex items-center gap-2 min-w-0">
        {result.status === 'resolved' ? (
          <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
        ) : result.status === 'mismatched' ? (
          <AlertTriangle className="h-4 w-4 flex-shrink-0 text-[var(--theme-warning)]" />
        ) : result.status === 'unverified' ? (
          <HelpCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-info)]" />
        ) : (
          <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
        )}
        <span className="text-sm text-themed-primary break-all">{result.originalEntry}</span>
      </div>
      <div className="flex items-center gap-2 flex-shrink-0">
        {resolvedInfo}
        {tag}
      </div>
    </div>
  );
};

export default DomainLeafRow;
