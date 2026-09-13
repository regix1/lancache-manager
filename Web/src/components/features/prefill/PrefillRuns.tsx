import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import Badge from '@components/ui/Badge';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatCount } from '@utils/formatters';
import { PrefillProgressCard } from './PrefillProgressCard';
import { getPrefillRunProgress, isPrefillRunActive, type PrefillRun } from './hooks/prefillTypes';

interface PrefillRunsProps {
  runs?: PrefillRun[];
  activeCount?: number;
  disabled?: boolean;
  onCancel?: (runId: string) => void;
  cancellingRunIds?: string[];
  runErrors?: Record<string, string>;
  emptyHelp?: string;
  children?: ReactNode;
}

export function PrefillRuns({
  runs = [],
  activeCount = 0,
  disabled = false,
  onCancel,
  cancellingRunIds,
  runErrors,
  emptyHelp,
  children
}: PrefillRunsProps) {
  const { t } = useTranslation();
  const [historyOpen, setHistoryOpen] = useState(false);
  const active = runs.filter(isPrefillRunActive);
  const count = Math.max(activeCount, active.length);
  const history = runs
    .filter((run) => !isPrefillRunActive(run))
    .sort((left, right) => right.snapshot.startedAt.localeCompare(left.snapshot.startedAt));

  return (
    <div className="prefill-runs">
      <section aria-label={t('prefill.runs.active')} className="prefill-runs__list">
        <h4 className="prefill-runs__label">
          {t('prefill.runs.active')}
          <Badge variant="neutral" className="badge-count">
            {formatCount(count)}
          </Badge>
        </h4>
        {active.length > 0 ? (
          active.map((run) => (
            <PrefillProgressCard
              key={`${run.sessionId}:${run.daemonInstanceId}:${run.runId}`}
              run={run}
              progress={getPrefillRunProgress(run)}
              onCancel={onCancel ? () => onCancel(run.runId) : undefined}
              isCancelling={run.cancelRequested || Boolean(cancellingRunIds?.includes(run.runId))}
              error={runErrors?.[run.runId]}
              disabled={disabled}
            />
          ))
        ) : children ? (
          children
        ) : count > 0 ? (
          <p className="prefill-runs__empty" role="status">
            <LoadingSpinner inline size="xs" />
            {t('common.loading')}
          </p>
        ) : (
          <div className="prefill-runs__empty">
            <p className="prefill-runs__empty-title">{t('prefill.runs.noActive')}</p>
            {emptyHelp && <p>{emptyHelp}</p>}
          </div>
        )}
      </section>
      <AccordionSection
        title={t('prefill.runs.history')}
        count={history.length}
        surface="well"
        isExpanded={historyOpen}
        onToggle={() => setHistoryOpen((open) => !open)}
      >
        <div className="prefill-runs__list" inert={!historyOpen}>
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
            <p className="prefill-runs__empty">{t('prefill.runs.noHistory')}</p>
          )}
        </div>
      </AccordionSection>
    </div>
  );
}
