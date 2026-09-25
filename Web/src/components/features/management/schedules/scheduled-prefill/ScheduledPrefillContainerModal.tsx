import { useTranslation } from 'react-i18next';
import { useConnectionLost } from '@hooks/useConnectionLost';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { ScheduledPrefillPersistentCard } from './ScheduledPrefillPersistentCard';
import type { ScheduledPrefillServiceKey } from './types';
import type { useScheduledPrefillContainers } from './useScheduledPrefillContainers';

interface ScheduledPrefillContainerModalProps {
  serviceKey: ScheduledPrefillServiceKey | null;
  containers: ReturnType<typeof useScheduledPrefillContainers>;
  disabled: boolean;
  justLoggedIn: boolean;
  onLogout: () => void;
  onClose: () => void;
}
export function ScheduledPrefillContainerModal({
  serviceKey,
  containers,
  disabled,
  justLoggedIn,
  onLogout,
  onClose
}: ScheduledPrefillContainerModalProps) {
  const { t } = useTranslation();
  // While the connection banner is up every read fails for that reason, so the banner speaks for
  // the two load errors. A failed start, stop or sign-in still shows its own alert.
  const connectionLost = useConnectionLost();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const service = serviceKey ? t(`prefill.persistent.services.${serviceKey}`) : '';
  return (
    <Modal
      opened={serviceKey !== null}
      onClose={onClose}
      title={service}
      size="lg"
      bodyFlexLayout
      className="scheduled-prefill-content-dialog"
    >
      <div className="scheduled-prefill-config-modal">
        <div className="scheduled-prefill-config-modal__scroll-area">
          <CustomScrollbar
            maxHeight="none"
            className="scheduled-prefill-config-modal__viewport"
            railPlacement="outer"
            radius="none"
          >
            <div className="scheduled-prefill-config-modal__scroll-content">
              <p className="text-sm text-themed-muted">
                {t(`${baseKey}.containerModalDescription`, { service })}
              </p>
              {containers.persistentError && !connectionLost && (
                <ErrorBlock
                  title={t(`${baseKey}.serviceStatus.loadFailed`)}
                  message={containers.persistentError}
                  retryLabel={t('common.retry')}
                  onRetry={() => void containers.loadPersistentContainers()}
                />
              )}
              {containers.persistentContainers === null && containers.persistentError !== null && (
                <p className="scheduled-prefill-persistent-card__hint">
                  {t(`${baseKey}.serviceStatus.hiddenUntilLoaded`)}
                </p>
              )}
              {serviceKey && (
                <ScheduledPrefillPersistentCard
                  serviceKey={serviceKey}
                  container={containers.containersByServiceKey.get(serviceKey)}
                  disabled={disabled}
                  listLoaded={containers.persistentContainers !== null}
                  listFailed={containers.persistentError !== null}
                  justLoggedIn={justLoggedIn}
                  actionError={containers.errors[serviceKey]}
                  actionNotice={containers.errorActions[serviceKey] === 'logoutRestarted'}
                  integrationLoginError={
                    connectionLost
                      ? undefined
                      : containers.visibleIntegrationLoginErrors[serviceKey]
                  }
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
                  onLogout={onLogout}
                  onLogin={(reuse) => void containers.handlePersistentLogin(serviceKey, reuse)}
                />
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
