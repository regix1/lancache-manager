import React from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { HelpPopover } from '@components/ui/HelpPopover';
import { formatRelativeTime } from '@utils/formatters';
import type { StatusCheckDomainsSource } from '@services/api.service';
import { formatRepoShortName } from './helpers';

interface DomainSourceFooterProps {
  source: StatusCheckDomainsSource | null;
  onRefresh: () => void;
  refreshing: boolean;
  refreshError: string | null;
  reloadError: string | null;
}

const DomainSourceFooter: React.FC<DomainSourceFooterProps> = ({
  source,
  onRefresh,
  refreshing,
  refreshError,
  reloadError
}) => {
  const { t } = useTranslation();
  const keys = 'management.sections.statusCheck';

  const parts = source
    ? [
        t(`${keys}.footerSource`, {
          repo: formatRepoShortName(source.repoUrl),
          branch: source.branch
        }),
        source.fetchedAtUtc && source.fromCache
          ? t(`${keys}.footerCachedCopy`, { time: formatRelativeTime(source.fetchedAtUtc) })
          : source.fetchedAtUtc
            ? t(`${keys}.footerFetched`, { time: formatRelativeTime(source.fetchedAtUtc) })
            : source.fromCache
              ? t(`${keys}.footerFromCache`)
              : null,
        source.noFetch ? t(`${keys}.footerNoFetch`) : null
      ].filter((part): part is string => part !== null)
    : null;
  // Built-in defaults are the usual case and go unsaid; a container or file is named behind the
  // help icon at the end of the line.
  const envHelp = !source
    ? null
    : source.envSource === 'dockerInspect'
      ? { label: t(`${keys}.footerEnvDockerLabel`), text: t(`${keys}.footerEnvDocker`) }
      : source.envSource === 'envFile' && source.envFilePath
        ? {
            label: t(`${keys}.footerEnvPathLabel`),
            text: t(`${keys}.footerEnvPath`, { path: source.envFilePath })
          }
        : null;

  return (
    <div>
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
        {/* Breaks inside a word only for a token too long for a line. */}
        <p className="mgmt-scanmeta min-w-0 status-check-help-line">
          {parts
            ? t(`${keys}.footerLine`, { parts: parts.join(t(`${keys}.footerSeparator`)) })
            : t(`${keys}.footerNotLoaded`)}
          {envHelp && (
            <HelpPopover width={320} aria-label={envHelp.label}>
              {envHelp.text}
            </HelpPopover>
          )}
        </p>
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
        <p className="text-xs text-themed-warning mt-1">
          {t(`${keys}.refreshFailed`, { error: refreshError })}
        </p>
      )}
      {reloadError && (
        <p className="text-xs text-themed-warning mt-1">
          {t(`${keys}.reloadFailed`, { error: reloadError })}
        </p>
      )}
      {!refreshError && source?.error && (
        <p className="text-xs text-themed-warning mt-1">
          {t(`${keys}.footerError`, { error: source.error })}
        </p>
      )}
    </div>
  );
};

export default DomainSourceFooter;
