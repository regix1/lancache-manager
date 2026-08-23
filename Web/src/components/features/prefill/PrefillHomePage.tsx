import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../ui/Button';
import { CollapsibleRegion } from '../../ui/CollapsibleRegion';
import { Shield, AlertCircle, ChevronDown } from 'lucide-react';
import { useMediaQuery } from '@hooks/useMediaQuery';
import type { GameServiceId } from '@/types/gameService';
import { PREFILL_SERVICES, type PrefillServiceConfig } from './hooks/prefillServiceConfig';
import './PrefillHomePage.css';

interface ServiceFeatureListProps {
  items: string[];
}

/* The three feature bullets start collapsed on small viewports to keep each card's
   Start Session action above the fold; wider viewports always show them and hide the
   toggle via CSS. */
function ServiceFeatureList({ items }: ServiceFeatureListProps) {
  const { t } = useTranslation();
  const isWideViewport = useMediaQuery('(min-width: 769px)');
  const [open, setOpen] = useState(false);

  return (
    <div className="prefill-service-features-region">
      <Button
        type="button"
        variant="transparent"
        open={open}
        onClick={() => setOpen(!open)}
        aria-expanded={isWideViewport || open}
        className="focus-ring prefill-features-toggle"
      >
        <span>{t('prefill.home.featuresToggle')}</span>
        <ChevronDown
          className={`h-4 w-4 transition-transform duration-200 ${open ? 'rotate-180' : ''}`}
        />
      </Button>
      <CollapsibleRegion open={isWideViewport || open}>
        <ul className="prefill-service-features">
          {items.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      </CollapsibleRegion>
    </div>
  );
}

interface PrefillHomePageProps {
  onServiceStart: (serviceId: GameServiceId) => void;
  error: string | null;
  errorService: GameServiceId;
  isAdmin: boolean;
  steamPrefillEnabled: boolean;
  epicPrefillEnabled: boolean;
  battlenetPrefillEnabled: boolean;
  riotPrefillEnabled: boolean;
  xboxPrefillEnabled: boolean;
}

export function PrefillHomePage({
  onServiceStart,
  error,
  errorService,
  isAdmin,
  steamPrefillEnabled,
  epicPrefillEnabled,
  battlenetPrefillEnabled,
  riotPrefillEnabled,
  xboxPrefillEnabled
}: PrefillHomePageProps) {
  const { t } = useTranslation();

  // The caller still passes one flag per service; fold them into a lookup so the cards
  // below stay driven by PREFILL_SERVICES rather than by a fixed list of props.
  const enabledByService = useMemo<Record<GameServiceId, boolean>>(
    () => ({
      steam: steamPrefillEnabled,
      epic: epicPrefillEnabled,
      battlenet: battlenetPrefillEnabled,
      riot: riotPrefillEnabled,
      xbox: xboxPrefillEnabled
    }),
    [
      steamPrefillEnabled,
      epicPrefillEnabled,
      battlenetPrefillEnabled,
      riotPrefillEnabled,
      xboxPrefillEnabled
    ]
  );

  // Services a guest has access to (admins always see all cards).
  const enabledServices = useMemo<readonly PrefillServiceConfig[]>(
    () => PREFILL_SERVICES.filter((service: PrefillServiceConfig) => enabledByService[service.id]),
    [enabledByService]
  );

  // If the user is a guest with access to exactly one service, skip the home page
  // and go directly to that service's panel.
  // Don't auto-redirect if there's already an error (e.g. Docker not running) to
  // avoid an infinite redirect loop.
  useEffect(() => {
    if (isAdmin) return;
    if (error) return;
    if (enabledServices.length !== 1) return;
    onServiceStart(enabledServices[0].id);
  }, [isAdmin, enabledServices, onServiceStart, error]);

  // If a guest only has one service, the effect above fires immediately, so
  // we render nothing to avoid a flash of the home page.
  // Show the home page if there's an error so the user can see it.
  if (!isAdmin && enabledServices.length === 1 && !error) {
    return null;
  }

  return (
    <div className="prefill-home">
      <div className="prefill-home-header">
        <h1 className="prefill-home-title">{t('prefill.home.title')}</h1>
        <p className="prefill-home-subtitle">{t('prefill.home.subtitle')}</p>
      </div>

      <div className="prefill-home-grid">
        {PREFILL_SERVICES.filter(
          (service: PrefillServiceConfig) => isAdmin || enabledByService[service.id]
        ).map((service: PrefillServiceConfig) => {
          const ServiceIcon = service.icon;
          return (
            <div key={service.id} className={`prefill-service-card ${service.homeCardClass}`}>
              <div className="prefill-service-card-top">
                <div className="icon-box icon-box--lg prefill-service-icon">
                  <ServiceIcon size={28} className="text-white" />
                </div>
                <div className="prefill-service-meta">
                  <h2 className="prefill-service-name">{service.displayName}</h2>
                  <div className="caps-label prefill-service-status">
                    <span>{t('prefill.home.ready')}</span>
                  </div>
                </div>
              </div>

              <p className="prefill-service-description">{t(service.homeDescriptionKey)}</p>

              <ServiceFeatureList items={service.homeFeatureKeys.map((key: string) => t(key))} />

              {error && errorService === service.id && (
                <div className="prefill-service-error">
                  <AlertCircle size={16} />
                  <span>{error}</span>
                </div>
              )}

              <div className="prefill-service-action">
                <span className="prefill-service-note">
                  <Shield size={14} />
                  {t(service.homeLoginNoteKey)}
                </span>
                <Button
                  variant="filled"
                  color="run"
                  size="md"
                  onClick={() => onServiceStart(service.id)}
                >
                  {t('prefill.home.startSession')}
                </Button>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
