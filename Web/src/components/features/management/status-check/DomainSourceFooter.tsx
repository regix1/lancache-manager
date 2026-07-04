import React from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { formatRelativeTime } from '@utils/formatters';
import type { StatusCheckDomainsSource } from '@services/api.service';
import { formatRepoShortName } from './helpers';

interface DomainSourceFooterProps {
  source: StatusCheckDomainsSource | null;
  onRefresh: () => void;
  refreshing: boolean;
  refreshError: string | null;
}

const DomainSourceFooter: React.FC<DomainSourceFooterProps> = ({
  source,
  onRefresh,
  refreshing,
  refreshError
}) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';

  const parts = source
    ? [
        t(`${keys}.footerSource`, {
          repo: formatRepoShortName(source.repoUrl),
          branch: source.branch
        }),
        source.envSource === 'dockerInspect'
          ? t(`${keys}.footerEnvDocker`)
          : source.envSource === 'envFile' && source.envFilePath
            ? t(`${keys}.footerEnvPath`, { path: source.envFilePath })
            : t(`${keys}.footerEnvDefaults`),
        source.fetchedAtUtc
          ? t(`${keys}.footerFetched`, { time: formatRelativeTime(source.fetchedAtUtc) })
          : null,
        source.fromCache ? t(`${keys}.footerFromCache`) : null,
        source.noFetch ? t(`${keys}.footerNoFetch`) : null
      ].filter((part): part is string => part !== null)
    : [t(`${keys}.footerNotLoaded`)];

  return (
    <div>
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
        <p className="text-xs text-themed-muted break-all">{parts.join(' · ')}</p>
        <Button
          variant="subtle"
          size="xs"
          className="flex-shrink-0"
          loading={refreshing}
          onClick={onRefresh}
        >
          {t(`${keys}.refreshList`)}
        </Button>
      </div>
      {refreshError && (
        <p className="text-xs text-[var(--theme-warning)] mt-1">
          {t(`${keys}.refreshFailed`, { error: refreshError })}
        </p>
      )}
      {!refreshError && source?.error && (
        <p className="text-xs text-[var(--theme-warning)] mt-1">
          {t(`${keys}.footerError`, { error: source.error })}
        </p>
      )}
    </div>
  );
};

export default DomainSourceFooter;
