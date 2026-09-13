import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { PrefillRuns } from '@components/features/prefill/PrefillRuns';
import { supportsConcurrentPrefill } from '@components/features/prefill/hooks/prefillTypes';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import type { ScheduledPrefillServiceKey } from './types';
import { formatBytes } from '@utils/formatters';

interface ScheduledPrefillDownloadsProps {
  serviceKey: ScheduledPrefillServiceKey;
  container?: PersistentPrefillContainerDto;
  disabled: boolean;
  onCancelDownload: (runId?: string) => void;
  cancellingRunIds?: string[];
  runErrors?: Record<string, string>;
}

export function ScheduledPrefillDownloads({
  serviceKey,
  container,
  disabled,
  onCancelDownload,
  cancellingRunIds,
  runErrors
}: ScheduledPrefillDownloadsProps) {
  const { t } = useTranslation();

  return (
    <div className="scheduled-prefill-downloads">
      <header className="scheduled-prefill-downloads__heading">
        <p>
          {t('prefill.runs.scope', {
            service: t(
              `management.schedules.services.scheduledPrefill.config.services.${serviceKey}`
            )
          })}
        </p>
      </header>
      <PrefillRuns
        runs={container?.runs}
        activeCount={Math.max(container?.activeRunCount ?? 0, container?.isPrefilling ? 1 : 0)}
        onCancel={onCancelDownload}
        cancellingRunIds={cancellingRunIds}
        runErrors={runErrors}
        disabled={disabled}
        emptyHelp={t('prefill.runs.noActiveHelp')}
      >
        {container?.isPrefilling && !supportsConcurrentPrefill(container) && (
          <div className="prefill-runs__list">
            <p className="text-sm text-themed-secondary">
              {t(
                'management.schedules.services.scheduledPrefill.config.persistentContainer.downloadProgressGeneric',
                {
                  bytes: formatBytes(container.totalBytesTransferred ?? 0)
                }
              )}
              {container.currentAppName && <span> · {container.currentAppName}</span>}
            </p>
            <Button
              color="stop"
              variant="filled"
              onClick={() => onCancelDownload()}
              disabled={disabled}
            >
              {t('common.cancel')}
            </Button>
          </div>
        )}
      </PrefillRuns>
    </div>
  );
}
