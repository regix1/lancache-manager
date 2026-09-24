import { useTranslation } from 'react-i18next';
import { useConnectionLost } from '@hooks/useConnectionLost';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { supportsConcurrentPrefill } from '@components/features/prefill/hooks/prefillTypes';
import { SCHEDULED_PREFILL_SERVICE_RUN_ORDER } from './constants';
import { ScheduledPrefillDownloads } from './ScheduledPrefillDownloads';
import type { useScheduledPrefillContainers } from './useScheduledPrefillContainers';

interface ScheduledPrefillActivityModalProps {
  opened: boolean;
  containers: ReturnType<typeof useScheduledPrefillContainers>;
  disabled: boolean;
  onClose: () => void;
}
export function ScheduledPrefillActivityModal({
  opened,
  containers,
  disabled,
  onClose
}: ScheduledPrefillActivityModalProps) {
  const { t } = useTranslation();
  // While the connection banner is up every read fails for that reason, so the banner speaks for
  // the load alert.
  const connectionLost = useConnectionLost();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t(`${baseKey}.activityTitle`)}
      size="xl"
      bodyFlexLayout
      className="scheduled-prefill-dialog scheduled-prefill-activity-dialog"
    >
      <div className="scheduled-prefill-config-modal">
        <p className="text-sm text-themed-muted">{t(`${baseKey}.activityDescription`)}</p>
        <div className="scheduled-prefill-config-modal__scroll-area">
          <CustomScrollbar
            maxHeight="none"
            className="scheduled-prefill-config-modal__viewport"
            radius="none"
          >
            <div className="scheduled-prefill-activity">
              {containers.persistentError && !connectionLost && (
                <Alert color="red">
                  {t(`${baseKey}.summaryError`, { error: containers.persistentError })}
                </Alert>
              )}
              {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => {
                const container = containers.containersByServiceKey.get(serviceKey);
                return (
                  <section key={serviceKey} className="scheduled-prefill-activity__service">
                    <h3>{t(`${baseKey}.services.${serviceKey}`)}</h3>
                    {container && supportsConcurrentPrefill(container) && (
                      <div className="scheduled-prefill-activity__capacity">
                        <p className="text-sm text-themed-muted">
                          {t('prefill.runs.capacity', {
                            count: container.activeRunCount ?? 0,
                            limit: container.maxConcurrentRuns ?? 1
                          })}
                        </p>
                        <p className="text-xs text-themed-muted">
                          {t('prefill.runs.capacityHelp')}
                        </p>
                      </div>
                    )}
                    <ScheduledPrefillDownloads
                      serviceKey={serviceKey}
                      container={container}
                      disabled={disabled}
                      cancellingRunIds={containers.cancellingRunIds}
                      runErrors={containers.runErrors}
                      onCancelDownload={(runId) =>
                        void containers.handleCancelPersistentDownload(serviceKey, runId)
                      }
                    />
                  </section>
                );
              })}
            </div>
          </CustomScrollbar>
        </div>
        <div className="scheduled-prefill-config-modal__actions">
          <Button onClick={onClose}>{t('common.close')}</Button>
        </div>
      </div>
    </Modal>
  );
}
