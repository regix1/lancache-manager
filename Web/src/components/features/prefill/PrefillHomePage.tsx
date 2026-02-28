import { useTranslation } from 'react-i18next';
import { Button } from '../../ui/Button';
import { SteamIcon } from '@components/ui/SteamIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { Play, Shield, AlertCircle } from 'lucide-react';
import type { GameServiceId } from '@/types/gameService';

interface PrefillHomePageProps {
  onServiceStart: (serviceId: GameServiceId) => void;
  error: string | null;
  errorService: GameServiceId;
}

export function PrefillHomePage({ onServiceStart, error, errorService }: PrefillHomePageProps) {
  const { t } = useTranslation();

  return (
    <div className="prefill-home">
      <div className="prefill-home-header">
        <h1 className="prefill-home-title">
          {t('prefill.home.title', 'Game Prefill')}
        </h1>
        <p className="prefill-home-subtitle">
          {t('prefill.home.subtitle', 'Pre-download game content to your lancache before the event. Choose a platform to start a prefill session.')}
        </p>
      </div>

      <div className="prefill-home-grid">
        {/* Steam Card */}
        <div className="prefill-service-card prefill-service-card--steam">
          <div className="prefill-service-card-top">
            <div className="prefill-service-icon">
              <SteamIcon size={28} className="text-white" />
            </div>
            <div className="prefill-service-meta">
              <h2 className="prefill-service-name">Steam</h2>
              <div className="prefill-service-status">
                <span className="prefill-service-status-dot prefill-service-status-dot--ready" />
                <span>{t('prefill.home.ready', 'Ready')}</span>
              </div>
            </div>
          </div>

          <p className="prefill-service-description">
            {t('prefill.home.steamDescription', 'Prefill your Steam library including recent games, top titles, or hand-picked selections from your account.')}
          </p>

          <ul className="prefill-service-features">
            <li>{t('prefill.home.steamFeature1', 'Prefill entire library or select specific games')}</li>
            <li>{t('prefill.home.steamFeature2', 'Recent and top games presets')}</li>
            <li>{t('prefill.home.steamFeature3', 'Force re-download and cache management')}</li>
          </ul>

          {error && errorService === 'steam' && (
            <div className="prefill-service-error">
              <AlertCircle size={16} />
              <span>{error}</span>
            </div>
          )}

          <div className="prefill-service-action">
            <span className="prefill-service-note">
              <Shield size={14} />
              {t('prefill.home.requiresSteamLogin', 'Requires Steam login')}
            </span>
            <Button
              variant="filled"
              size="md"
              onClick={() => onServiceStart('steam')}
            >
              <Play size={16} />
              {t('prefill.home.startSession', 'Start Session')}
            </Button>
          </div>
        </div>

        {/* Epic Games Card */}
        <div className="prefill-service-card prefill-service-card--epic">
          <div className="prefill-service-card-top">
            <div className="prefill-service-icon">
              <EpicIcon size={28} className="text-white" />
            </div>
            <div className="prefill-service-meta">
              <h2 className="prefill-service-name">Epic Games</h2>
              <div className="prefill-service-status">
                <span className="prefill-service-status-dot prefill-service-status-dot--ready" />
                <span>{t('prefill.home.ready', 'Ready')}</span>
              </div>
            </div>
          </div>

          <p className="prefill-service-description">
            {t('prefill.home.epicDescription', 'Prefill Epic Games Store titles to your cache. Connect your Epic account and select games to pre-download.')}
          </p>

          <ul className="prefill-service-features">
            <li>{t('prefill.home.epicFeature1', 'Browse and select from your Epic library')}</li>
            <li>{t('prefill.home.epicFeature2', 'Prefill selected or all owned games')}</li>
            <li>{t('prefill.home.epicFeature3', 'Device code authentication')}</li>
          </ul>

          {error && errorService === 'epic' && (
            <div className="prefill-service-error">
              <AlertCircle size={16} />
              <span>{error}</span>
            </div>
          )}

          <div className="prefill-service-action">
            <span className="prefill-service-note">
              <Shield size={14} />
              {t('prefill.home.requiresEpicLogin', 'Requires Epic Games login')}
            </span>
            <Button
              variant="filled"
              size="md"
              onClick={() => onServiceStart('epic')}
            >
              <Play size={16} />
              {t('prefill.home.startSession', 'Start Session')}
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}
