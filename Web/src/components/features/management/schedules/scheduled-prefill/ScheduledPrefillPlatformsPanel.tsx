import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import Badge from '@components/ui/Badge';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import { SCHEDULED_PREFILL_SERVICE_RUN_ORDER } from './constants';
import { ScheduledPrefillPlatformSection } from './ScheduledPrefillPlatformSection';
import {
  SCHEDULED_PREFILL_PLATFORM_UI,
  isScheduledPrefillAccountService,
  needsPersistentLogin
} from './scheduledPrefillPlatformUi';
import type { ScheduledPrefillPersistentActionState } from './scheduledPrefillPersistentTypes';
import type {
  ScheduledPrefillConfigDto,
  ScheduledPrefillServiceConfigDto,
  ScheduledPrefillServiceKey
} from './types';

interface ScheduledPrefillPlatformsPanelProps {
  config: ScheduledPrefillConfigDto;
  disabled?: boolean;
  statusLoading?: boolean;
  containersByServiceKey: Map<ScheduledPrefillServiceKey, PersistentPrefillContainerDto>;
  selectedGamesCountByServiceKey: Record<ScheduledPrefillServiceKey, number>;
  persistentAction: ScheduledPrefillPersistentActionState | null;
  authenticatingServiceKeys: ScheduledPrefillServiceKey[];
  gameSelectionLoadingServiceKey: ScheduledPrefillServiceKey | null;
  onServiceChange: (
    serviceKey: ScheduledPrefillServiceKey,
    serviceConfig: ScheduledPrefillServiceConfigDto
  ) => void;
  onStart: (serviceKey: ScheduledPrefillServiceKey) => void;
  onStop: (serviceKey: ScheduledPrefillServiceKey) => void;
  onLogin: (serviceKey: ScheduledPrefillServiceKey) => void;
  onSelectGames: (serviceKey: ScheduledPrefillServiceKey) => void;
  onDownload: (serviceKey: ScheduledPrefillServiceKey) => void;
  onCancelDownload: (serviceKey: ScheduledPrefillServiceKey) => void;
}

export function ScheduledPrefillPlatformsPanel({
  config,
  disabled = false,
  statusLoading = false,
  containersByServiceKey,
  selectedGamesCountByServiceKey,
  persistentAction,
  authenticatingServiceKeys,
  gameSelectionLoadingServiceKey,
  onServiceChange,
  onStart,
  onStop,
  onLogin,
  onSelectGames,
  onDownload,
  onCancelDownload
}: ScheduledPrefillPlatformsPanelProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const [activeServiceKey, setActiveServiceKey] = useState<ScheduledPrefillServiceKey>('steam');

  const getNavHint = (serviceKey: ScheduledPrefillServiceKey): string | null => {
    const serviceConfig = config[serviceKey];
    // The container list loads independently of config, so don't flag a false "needs login" hint
    // while it's still loading (or hasn't loaded) - we simply don't know its state yet.
    if (!serviceConfig.enabled || statusLoading) {
      return null;
    }

    if (isScheduledPrefillAccountService(serviceKey)) {
      const container = containersByServiceKey.get(serviceKey);
      if (needsPersistentLogin(container)) {
        return t(`${baseKey}.platforms.nav.loginRequired`);
      }
    }

    return null;
  };

  return (
    <section className="scheduled-prefill-platforms-panel">
      <div className="scheduled-prefill-platforms">
        <nav
          className="scheduled-prefill-platforms__nav"
          aria-label={t(`${baseKey}.platforms.navLabel`)}
        >
          {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => {
            const serviceConfig = config[serviceKey];
            const platformMeta = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];
            const PlatformIcon = platformMeta.icon;
            const isActive = activeServiceKey === serviceKey;
            const navHint = getNavHint(serviceKey);
            const container = containersByServiceKey.get(serviceKey);

            return (
              <button
                key={serviceKey}
                type="button"
                className={`scheduled-prefill-platforms__nav-item${
                  isActive ? ' scheduled-prefill-platforms__nav-item--active' : ''
                } ${platformMeta.rowClassName}`}
                aria-current={isActive ? 'page' : undefined}
                onClick={() => setActiveServiceKey(serviceKey)}
              >
                <span className="scheduled-prefill-platforms__nav-icon" aria-hidden="true">
                  <PlatformIcon size={18} />
                </span>
                <span className="scheduled-prefill-platforms__nav-text">
                  <span className="scheduled-prefill-platforms__nav-label">
                    {t(`${baseKey}.services.${serviceKey}`)}
                  </span>
                  {navHint && (
                    <span className="scheduled-prefill-platforms__nav-hint">{navHint}</span>
                  )}
                </span>
                <span className="scheduled-prefill-platforms__nav-badges">
                  {serviceConfig.enabled && (
                    <Badge variant="success" className="scheduled-prefill-platforms__nav-badge">
                      {t(`${baseKey}.platforms.status.on`)}
                    </Badge>
                  )}
                  {container?.isRunning && (
                    <Badge variant="info" className="scheduled-prefill-platforms__nav-badge">
                      {t(`${baseKey}.platforms.status.containerShort`)}
                    </Badge>
                  )}
                </span>
              </button>
            );
          })}
        </nav>

        <div className="scheduled-prefill-platforms__content">
          <ScheduledPrefillPlatformSection
            key={activeServiceKey}
            serviceKey={activeServiceKey}
            config={config[activeServiceKey]}
            disabled={disabled}
            statusLoading={statusLoading}
            container={containersByServiceKey.get(activeServiceKey)}
            selectedGamesCount={selectedGamesCountByServiceKey[activeServiceKey]}
            persistentAction={persistentAction}
            authenticating={authenticatingServiceKeys.includes(activeServiceKey)}
            gameSelectionLoading={gameSelectionLoadingServiceKey === activeServiceKey}
            onChange={(serviceConfig) => onServiceChange(activeServiceKey, serviceConfig)}
            onStart={() => onStart(activeServiceKey)}
            onStop={() => onStop(activeServiceKey)}
            onLogin={() => onLogin(activeServiceKey)}
            onSelectGames={() => onSelectGames(activeServiceKey)}
            onDownload={() => onDownload(activeServiceKey)}
            onCancelDownload={() => onCancelDownload(activeServiceKey)}
          />
        </div>
      </div>
    </section>
  );
}
