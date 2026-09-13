import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { PrefillProgressCard } from '@components/features/prefill/PrefillProgressCard';
import {
  getPrefillRunProgress,
  isPrefillRunActive,
  supportsConcurrentPrefill
} from '@components/features/prefill/hooks/prefillTypes';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import type { ScheduledPrefillServiceKey } from './types';
import { formatBytes } from '@utils/formatters';
import LoadingSpinner from '@components/common/LoadingSpinner';

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
  const [historyOpen, setHistoryOpen] = useState(false);
  const historyId = useId();
  const runs = container?.runs ?? [];
  const active = runs.filter(isPrefillRunActive);
  const activeCount = Math.max(
    container?.activeRunCount ?? 0,
    active.length,
    container?.isPrefilling ? 1 : 0
  );
  const history = runs
    .filter((run) => !isPrefillRunActive(run))
    .sort((left, right) => right.snapshot.startedAt.localeCompare(left.snapshot.startedAt));

  return (
    <div className="scheduled-prefill-downloads">
      <header className="scheduled-prefill-downloads__heading">
        <h4 className="scheduled-prefill-platform-block__title">{t('prefill.runs.downloads')}</h4>
        <p>
          {t('prefill.runs.scope', {
            service: t(
              `management.schedules.services.scheduledPrefill.config.services.${serviceKey}`
            )
          })}
        </p>
      </header>
      <section aria-label={t('prefill.runs.active')} className="scheduled-prefill-run-history">
        <h5 className="scheduled-prefill-downloads__label">
          {t('prefill.runs.active')} ({activeCount})
        </h5>
        {active.length > 0 ? (
          <div className="scheduled-prefill-run-history__list">
            {active.map((run) => (
              <PrefillProgressCard
                key={`${run.sessionId}:${run.daemonInstanceId}:${run.runId}`}
                run={run}
                progress={getPrefillRunProgress(run)}
                onCancel={() => onCancelDownload(run.runId)}
                isCancelling={run.cancelRequested || Boolean(cancellingRunIds?.includes(run.runId))}
                error={runErrors?.[run.runId]}
                disabled={disabled}
              />
            ))}
          </div>
        ) : container?.isPrefilling && !supportsConcurrentPrefill(container) ? (
          <div className="scheduled-prefill-run-history__list">
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
        ) : activeCount > 0 ? (
          <p className="scheduled-prefill-downloads__empty" role="status">
            <LoadingSpinner inline size="xs" />
            {t('common.loading')}
          </p>
        ) : (
          <p className="scheduled-prefill-downloads__empty">{t('prefill.runs.noActive')}</p>
        )}
      </section>
      <section className="scheduled-prefill-downloads__history">
        <Button
          variant="transparent"
          className="scheduled-prefill-downloads__toggle focus-ring"
          aria-expanded={historyOpen}
          aria-controls={historyId}
          onClick={() => setHistoryOpen((open) => !open)}
        >
          <span>
            {t('prefill.runs.history')} ({history.length})
          </span>
          <ChevronDown
            size={16}
            aria-hidden="true"
            className={`scheduled-prefill-container-settings__icon${historyOpen ? ' scheduled-prefill-container-settings__icon--open' : ''}`}
          />
        </Button>
        <div id={historyId} inert={!historyOpen}>
          <CollapsibleRegion
            open={historyOpen}
            className="prefill-run-details-region"
            contentClassName="scheduled-prefill-run-history__list"
          >
            {history.length > 0 ? (
              history.map((run) => (
                <PrefillProgressCard
                  key={`${run.sessionId}:${run.daemonInstanceId}:${run.runId}`}
                  run={run}
                  progress={getPrefillRunProgress(run)}
                  history
                />
              ))
            ) : (
              <p className="scheduled-prefill-downloads__empty">{t('prefill.runs.noHistory')}</p>
            )}
          </CollapsibleRegion>
        </div>
      </section>
    </div>
  );
}
