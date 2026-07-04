import React from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, CheckCircle2, HelpCircle, XCircle } from 'lucide-react';
import type { StatusCheckDomainResult } from '@services/api.service';

interface DomainLeafRowProps {
  result: StatusCheckDomainResult;
}

const DomainLeafRow: React.FC<DomainLeafRowProps> = ({ result }) => {
  const { t } = useTranslation();
  const isWildcardProbe = result.domain !== result.originalEntry;
  const testedAsNote = t('management.sections.statusCheck.testedAs', { domain: result.domain });
  const primaryIp = result.resolvedIps[0];

  return (
    <div>
      <div className="flex items-center justify-between gap-2 flex-wrap">
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
          <span
            className="text-sm text-themed-primary break-all"
            title={isWildcardProbe ? testedAsNote : undefined}
          >
            {result.originalEntry}
          </span>
        </div>
        {(result.status === 'resolved' || result.status === 'unverified') && primaryIp && (
          <span className="text-xs text-themed-muted tabular-nums">
            {primaryIp}
            {result.resolvedIps.length > 1 ? ` +${result.resolvedIps.length - 1}` : ''}
            {result.latencyMs !== null ? ` · ${result.latencyMs} ms` : ''}
            {result.heartbeatVerified && result.servedBy
              ? ` · ${t('management.sections.statusCheck.rowServedBy', { host: result.servedBy })}`
              : ''}
          </span>
        )}
      </div>
      {result.status === 'mismatched' && (
        <div className="ml-6 mt-1 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-warning-bg)] text-[var(--theme-warning-text)] tabular-nums">
          {result.expectedIps.length > 0
            ? t('management.sections.statusCheck.reasonMismatched', {
                actual: result.resolvedIps.join(', '),
                expected: result.expectedIps.join(', ')
              })
            : // Pre-v1.3 persisted results could carry mismatched rows with an empty expected
              // list - never render a dangling "expected ." clause for them.
              t('management.sections.statusCheck.reasonUnverified', {
                actual: result.resolvedIps.join(', ')
              })}
          {isWildcardProbe ? ` ${testedAsNote}` : ''}
        </div>
      )}
      {result.status === 'unverified' && (
        <div className="ml-6 mt-1 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-info-bg)] text-[var(--theme-info-text)] tabular-nums">
          {t('management.sections.statusCheck.reasonUnverified', {
            actual: result.resolvedIps.join(', ')
          })}
          {isWildcardProbe ? ` ${testedAsNote}` : ''}
        </div>
      )}
      {result.status === 'unresolved' && (
        <div className="ml-6 mt-1 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-error-bg)] text-[var(--theme-error-text)]">
          {t('management.sections.statusCheck.reasonUnresolved', {
            error: result.error ?? t('management.sections.statusCheck.unknownError')
          })}
          {isWildcardProbe ? ` ${testedAsNote}` : ''}
        </div>
      )}
    </div>
  );
};

export default DomainLeafRow;
