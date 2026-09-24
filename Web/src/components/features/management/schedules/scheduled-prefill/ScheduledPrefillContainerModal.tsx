import { useTranslation } from 'react-i18next';
import { useConnectionLost } from '@hooks/useConnectionLost';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { ScheduledPrefillPersistentCard } from './ScheduledPrefillPersistentCard';
import type { ScheduledPrefillServiceKey } from './types';
import type { useScheduledPrefillContainers } from './useScheduledPrefillContainers';

interface ScheduledPrefillContainerModalProps {
  serviceKey: ScheduledPrefillServiceKey | null;
  containers: ReturnType<typeof useScheduledPrefillContainers>;
  disabled: boolean;
  onClose: () => void;
}
export function ScheduledPrefillContainerModal({
  serviceKey,
  containers,
  disabled,
  onClose
}: ScheduledPrefillContainerModalProps) {
  const { t } = useTranslation();
  // While the connection banner is up every read fails for that reason, so the banner speaks for
  // the two load alerts. A failed start, stop or sign-in still shows its own alert.
  const connectionLost = useConnectionLost();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const service = serviceKey ? t(`${baseKey}.services.${serviceKey}`) : '';
  return (
    <Modal
      opened={serviceKey !== null}
      onClose={onClose}
      title={t(`${baseKey}.containerModalTitle`, { service })}
      size="lg"
      bodyFlexLayout
      className="scheduled-prefill-content-dialog scheduled-prefill-container-dialog"
    >
      <div className="scheduled-prefill-content-modal">
        <div className="scheduled-prefill-content-modal__scroll-area">
          <CustomScrollbar
            maxHeight="none"
            className="scheduled-prefill-content-modal__viewport"
            radius="none"
          >
            <div className="scheduled-prefill-dialog-content scheduled-prefill-content-modal__scroll-content">
              <p className="text-sm text-themed-muted">
                {t(`${baseKey}.containerModalDescription`, { service })}
              </p>
              {serviceKey && (
                <>
                  {containers.errors[serviceKey] && (
                    <Alert color="red">
                      {t(`${baseKey}.summaryError`, { error: containers.errors[serviceKey] })}
                    </Alert>
                  )}
                  {containers.persistentError && !connectionLost && (
                    <Alert color="red">
                      {t(`${baseKey}.summaryError`, { error: containers.persistentError })}
                    </Alert>
                  )}
                  {containers.visibleIntegrationLoginErrors[serviceKey] && !connectionLost && (
                    <Alert color="red">
                      {t(`${baseKey}.summaryError`, {
                        error: containers.visibleIntegrationLoginErrors[serviceKey]
                      })}
                    </Alert>
                  )}
                  <ScheduledPrefillPersistentCard
                    serviceKey={serviceKey}
                    container={containers.containersByServiceKey.get(serviceKey)}
                    disabled={disabled}
                    statusLoading={containers.loadingPersistentContainers}
                    authenticating={containers.authenticatingServiceKeys.some(
                      (key) => key === serviceKey
                    )}
                    integrationLoginAvailability={containers.visibleIntegrationLoginAvailabilityByService.get(
                      serviceKey
                    )}
                    integrationLoginAvailabilityLoading={
                      containers.loadingIntegrationLoginAvailability
                    }
                    action={containers.actions[serviceKey] ?? null}
                    onStart={() => void containers.handleStartPersistent(serviceKey)}
                    onStop={() => void containers.handleStopPersistent(serviceKey)}
                    onLogout={() => void containers.handleLogoutPersistent(serviceKey)}
                    onLogin={(reuse) => void containers.handlePersistentLogin(serviceKey, reuse)}
                  />
                </>
              )}
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
