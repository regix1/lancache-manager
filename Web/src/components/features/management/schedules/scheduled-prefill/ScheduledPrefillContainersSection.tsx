import { useTranslation } from 'react-i18next';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS } from './constants';
import { ScheduledPrefillPersistentCard } from './ScheduledPrefillPersistentCard';
import type { ScheduledPrefillContainersSectionProps } from './scheduledPrefillPersistentTypes';

export function ScheduledPrefillContainersSection({
  disabled = false,
  statusLoading = false,
  containersByServiceKey,
  selectedGamesCountByServiceKey,
  persistentAction,
  authenticatingServiceKeys,
  gameSelectionLoadingServiceKey,
  onStart,
  onStop,
  onLogin,
  onSelectGames,
  onDownload,
  onCancelDownload
}: ScheduledPrefillContainersSectionProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const containersKey = `${baseKey}.persistentContainers`;

  return (
    <section className="scheduled-prefill-containers">
      <div className="scheduled-prefill-containers__header">
        <div>
          <h3 className="scheduled-prefill-config-modal__section-title">
            {t(`${containersKey}.title`)}
          </h3>
          <p className="scheduled-prefill-config-modal__description">
            {t(`${containersKey}.description`)}
          </p>
        </div>
        {statusLoading && (
          <span className="scheduled-prefill-config-modal__inline-loading">
            <LoadingSpinner inline size="sm" />
            {t('prefill.persistent.loading')}
          </span>
        )}
      </div>

      <div className="scheduled-prefill-containers__grid">
        {SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.map((serviceKey) => {
          const rowAction =
            persistentAction?.serviceKey === serviceKey ? persistentAction.action : null;

          return (
            <ScheduledPrefillPersistentCard
              key={serviceKey}
              serviceKey={serviceKey}
              container={containersByServiceKey.get(serviceKey)}
              selectedGamesCount={selectedGamesCountByServiceKey[serviceKey] ?? 0}
              disabled={disabled}
              statusLoading={statusLoading}
              authenticating={authenticatingServiceKeys.includes(serviceKey)}
              action={rowAction}
              gameSelectionLoading={gameSelectionLoadingServiceKey === serviceKey}
              onStart={() => onStart(serviceKey)}
              onStop={() => onStop(serviceKey)}
              onLogin={() => onLogin(serviceKey)}
              onSelectGames={() => onSelectGames(serviceKey)}
              onDownload={() => onDownload(serviceKey)}
              onCancelDownload={() => onCancelDownload(serviceKey)}
            />
          );
        })}
      </div>
    </section>
  );
}
