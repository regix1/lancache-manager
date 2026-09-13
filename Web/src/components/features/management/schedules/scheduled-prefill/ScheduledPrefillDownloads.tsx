import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { AccordionSection } from '@components/ui/AccordionSection';
import { PrefillProgressCard } from '@components/features/prefill/PrefillProgressCard';
import {
  getPrefillRunProgress,
  isPrefillRunActive,
  supportsConcurrentPrefill
} from '@components/features/prefill/hooks/prefillTypes';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import type { ScheduledPrefillServiceKey } from './types';
import { formatBytes, formatCount } from '@utils/formatters';
import LoadingSpinner from '@components/common/LoadingSpinner';
import Badge from '@components/ui/Badge';

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
        <p>
          {t('prefill.runs.scope', {
            service: t(
              `management.schedules.services.scheduledPrefill.config.services.${serviceKey}`
            )
          })}
        </p>
      </header>
      <section aria-label={t('prefill.runs.active')} className="scheduled-prefill-run-history">
        <h4 className="scheduled-prefill-downloads__label">
          {t('prefill.runs.active')}
          <Badge variant="neutral" className="badge-count">
            {formatCount(activeCount)}
          </Badge>
        </h4>
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
          <div className="scheduled-prefill-downloads__empty">
            <p className="scheduled-prefill-downloads__empty-title">{t('prefill.runs.noActive')}</p>
            <p>{t('prefill.runs.noActiveHelp')}</p>
          </div>
        )}
      </section>
      <section className="scheduled-prefill-downloads__history">
        <AccordionSection
          title={t('prefill.runs.history')}
          count={history.length}
          surface="well"
          isExpanded={historyOpen}
          onToggle={() => setHistoryOpen((open) => !open)}
        >
          <div className="scheduled-prefill-run-history__list" inert={!historyOpen}>
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
          </div>
        </AccordionSection>
      </section>
    </div>
  );
}
