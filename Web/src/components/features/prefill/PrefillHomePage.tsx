import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../ui/Button';
import { CollapsibleRegion } from '../../ui/CollapsibleRegion';
import { SteamIcon } from '@components/ui/SteamIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { Shield, AlertCircle, ChevronDown } from 'lucide-react';
import { useMediaQuery } from '@hooks/useMediaQuery';
import type { GameServiceId } from '@/types/gameService';
import './PrefillHomePage.css';

interface ServiceFeatureListProps {
  items: string[];
}

/* The three feature bullets start collapsed on small viewports to keep each card's
   Start Session action above the fold; wider viewports always show them and hide the
   toggle via CSS. [30] */
function ServiceFeatureList({ items }: ServiceFeatureListProps) {
  const { t } = useTranslation();
  const isWideViewport = useMediaQuery('(min-width: 769px)');
  const [open, setOpen] = useState(false);

  return (
    <div className="prefill-service-features-region">
      <button
        type="button"
        onClick={() => setOpen(!open)}
        aria-expanded={isWideViewport || open}
        className="focus-ring prefill-features-toggle"
      >
        <span>{t('prefill.home.featuresToggle', "What's included")}</span>
        <ChevronDown
          className={`h-4 w-4 transition-transform duration-200 ${open ? 'rotate-180' : ''}`}
        />
      </button>
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

  const showSteam = isAdmin || steamPrefillEnabled;
  const showEpic = isAdmin || epicPrefillEnabled;
  const showBattlenet = isAdmin || battlenetPrefillEnabled;
  const showRiot = isAdmin || riotPrefillEnabled;
  const showXbox = isAdmin || xboxPrefillEnabled;

  // Number of services a guest has access to (admins always see all cards).
  const enabledServiceCount =
    Number(steamPrefillEnabled) +
    Number(epicPrefillEnabled) +
    Number(battlenetPrefillEnabled) +
    Number(riotPrefillEnabled) +
    Number(xboxPrefillEnabled);

  // If the user is a guest with access to exactly one service, skip the home page
  // and go directly to that service's panel.
  // Don't auto-redirect if there's already an error (e.g. Docker not running) to
  // avoid an infinite redirect loop.
  useEffect(() => {
    if (isAdmin) return;
    if (error) return;
    if (enabledServiceCount !== 1) return;
    if (steamPrefillEnabled) {
      onServiceStart('steam');
    } else if (epicPrefillEnabled) {
      onServiceStart('epic');
    } else if (battlenetPrefillEnabled) {
      onServiceStart('battlenet');
    } else if (riotPrefillEnabled) {
      onServiceStart('riot');
    } else if (xboxPrefillEnabled) {
      onServiceStart('xbox');
    }
  }, [
    isAdmin,
    steamPrefillEnabled,
    epicPrefillEnabled,
    battlenetPrefillEnabled,
    riotPrefillEnabled,
    xboxPrefillEnabled,
    enabledServiceCount,
    onServiceStart,
    error
  ]);

  // If a guest only has one service, the effect above fires immediately, so
  // we render nothing to avoid a flash of the home page.
  // Show the home page if there's an error so the user can see it.
  if (!isAdmin && enabledServiceCount === 1 && !error) {
    return null;
  }

  return (
    <div className="prefill-home">
      <div className="prefill-home-header">
        <h1 className="prefill-home-title">{t('prefill.home.title', 'Game Prefill')}</h1>
        <p className="prefill-home-subtitle">
          {t(
            'prefill.home.subtitle',
            'Pre-download game content to your lancache before the event. Choose a platform to start a prefill session.'
          )}
        </p>
      </div>

      <div className="prefill-home-grid">
        {/* Steam Card */}
        {showSteam && (
          <div className="prefill-service-card prefill-service-card--steam">
            <div className="prefill-service-card-top">
              <div className="icon-box icon-box--lg prefill-service-icon">
                <SteamIcon size={28} className="text-white" />
              </div>
              <div className="prefill-service-meta">
                <h2 className="prefill-service-name">Steam</h2>
                <div className="caps-label prefill-service-status">
                  <span>{t('prefill.home.ready', 'Ready')}</span>
                </div>
              </div>
            </div>

            <p className="prefill-service-description">
              {t(
                'prefill.home.steamDescription',
                'Prefill your Steam library including recent games, top titles, or hand-picked selections from your account.'
              )}
            </p>

            <ServiceFeatureList
              items={[
                t('prefill.home.steamFeature1', 'Prefill entire library or select specific games'),
                t('prefill.home.steamFeature2', 'Recent and top games presets'),
                t('prefill.home.steamFeature3', 'Force re-download and cache management')
              ]}
            />

            {error && errorService === 'steam' && (
              <div className="prefill-service-error">
                <AlertCircle size={16} />
                <span>{error}</span>
              </div>
            )}

            <div className="prefill-service-action">
              <span className="prefill-service-note">
                <Shield size={14} />
                {t('prefill.home.requiresSteamLogin', 'Username, password & Steam Guard')}
              </span>
              <Button variant="filled" size="md" onClick={() => onServiceStart('steam')}>
                {t('prefill.home.startSession', 'Start Session')}
              </Button>
            </div>
          </div>
        )}

        {/* Epic Games Card */}
        {showEpic && (
          <div className="prefill-service-card prefill-service-card--epic">
            <div className="prefill-service-card-top">
              <div className="icon-box icon-box--lg prefill-service-icon">
                <EpicIcon size={28} className="text-white" />
              </div>
              <div className="prefill-service-meta">
                <h2 className="prefill-service-name">Epic Games</h2>
                <div className="caps-label prefill-service-status">
                  <span>{t('prefill.home.ready', 'Ready')}</span>
                </div>
              </div>
            </div>

            <p className="prefill-service-description">
              {t(
                'prefill.home.epicDescription',
                'Prefill your Epic Games library including recent games, top titles, or hand-picked selections from your account.'
              )}
            </p>

            <ServiceFeatureList
              items={[
                t('prefill.home.epicFeature1', 'Prefill entire library or select specific games'),
                t('prefill.home.epicFeature2', 'Recent and top games presets'),
                t('prefill.home.epicFeature3', 'Force re-download and cache management')
              ]}
            />

            {error && errorService === 'epic' && (
              <div className="prefill-service-error">
                <AlertCircle size={16} />
                <span>{error}</span>
              </div>
            )}

            <div className="prefill-service-action">
              <span className="prefill-service-note">
                <Shield size={14} />
                {t('prefill.home.requiresEpicLogin', 'Browser-based authorization code login')}
              </span>
              <Button variant="filled" size="md" onClick={() => onServiceStart('epic')}>
                {t('prefill.home.startSession', 'Start Session')}
              </Button>
            </div>
          </div>
        )}

        {/* Battle.net Card */}
        {showBattlenet && (
          <div className="prefill-service-card prefill-service-card--battlenet">
            <div className="prefill-service-card-top">
              <div className="icon-box icon-box--lg prefill-service-icon">
                <BlizzardIcon size={28} className="text-white" />
              </div>
              <div className="prefill-service-meta">
                <h2 className="prefill-service-name">Battle.net</h2>
                <div className="caps-label prefill-service-status">
                  <span>{t('prefill.home.ready', 'Ready')}</span>
                </div>
              </div>
            </div>

            <p className="prefill-service-description">
              {t(
                'prefill.home.battlenetDescription',
                'Prefill public Blizzard CDN content for Battle.net titles such as World of Warcraft, Diablo, and Overwatch.'
              )}
            </p>

            <ServiceFeatureList
              items={[
                t(
                  'prefill.home.battlenetFeature1',
                  'Prefill all products or select specific titles'
                ),
                t('prefill.home.battlenetFeature2', 'No account or login required'),
                t('prefill.home.battlenetFeature3', 'Force re-download and cache management')
              ]}
            />

            {error && errorService === 'battlenet' && (
              <div className="prefill-service-error">
                <AlertCircle size={16} />
                <span>{error}</span>
              </div>
            )}

            <div className="prefill-service-action">
              <span className="prefill-service-note">
                <Shield size={14} />
                {t('prefill.home.battlenetNoLogin', 'No account login required')}
              </span>
              <Button variant="filled" size="md" onClick={() => onServiceStart('battlenet')}>
                {t('prefill.home.startSession', 'Start Session')}
              </Button>
            </div>
          </div>
        )}

        {/* Riot Games Card */}
        {showRiot && (
          <div className="prefill-service-card prefill-service-card--riot">
            <div className="prefill-service-card-top">
              <div className="icon-box icon-box--lg prefill-service-icon">
                <RiotIcon size={28} className="text-white" />
              </div>
              <div className="prefill-service-meta">
                <h2 className="prefill-service-name">Riot Games</h2>
                <div className="caps-label prefill-service-status">
                  <span>{t('prefill.home.ready', 'Ready')}</span>
                </div>
              </div>
            </div>

            <p className="prefill-service-description">
              {t(
                'prefill.home.riotDescription',
                'Prefill public Riot CDN content for titles such as League of Legends and VALORANT.'
              )}
            </p>

            <ServiceFeatureList
              items={[
                t('prefill.home.riotFeature1', 'Prefill all products or select specific titles'),
                t('prefill.home.riotFeature2', 'League of Legends and VALORANT content'),
                t('prefill.home.riotFeature3', 'Force re-download and cache management')
              ]}
            />

            {error && errorService === 'riot' && (
              <div className="prefill-service-error">
                <AlertCircle size={16} />
                <span>{error}</span>
              </div>
            )}

            <div className="prefill-service-action">
              <span className="prefill-service-note">
                <Shield size={14} />
                {t('prefill.home.riotNoLogin', 'No account login required')}
              </span>
              <Button variant="filled" size="md" onClick={() => onServiceStart('riot')}>
                {t('prefill.home.startSession', 'Start Session')}
              </Button>
            </div>
          </div>
        )}

        {/* Xbox Card */}
        {showXbox && (
          <div className="prefill-service-card prefill-service-card--xbox">
            <div className="prefill-service-card-top">
              <div className="icon-box icon-box--lg prefill-service-icon">
                <XboxIcon size={28} className="text-white" />
              </div>
              <div className="prefill-service-meta">
                <h2 className="prefill-service-name">Xbox</h2>
                <div className="caps-label prefill-service-status">
                  <span>{t('prefill.home.ready', 'Ready')}</span>
                </div>
              </div>
            </div>

            <p className="prefill-service-description">
              {t(
                'prefill.home.xboxDescription',
                'Prefill your Xbox and Microsoft Store library, pre-downloading game content for the titles in your account.'
              )}
            </p>

            <ServiceFeatureList
              items={[
                t('prefill.home.xboxFeature1', 'Prefill entire library or select specific games'),
                t('prefill.home.xboxFeature2', 'Microsoft Store and Game Pass titles'),
                t('prefill.home.xboxFeature3', 'Force re-download and cache management')
              ]}
            />

            {error && errorService === 'xbox' && (
              <div className="prefill-service-error">
                <AlertCircle size={16} />
                <span>{error}</span>
              </div>
            )}

            <div className="prefill-service-action">
              <span className="prefill-service-note">
                <Shield size={14} />
                {t('prefill.home.requiresXboxLogin', 'Browser-based device code login')}
              </span>
              <Button variant="filled" size="md" onClick={() => onServiceStart('xbox')}>
                {t('prefill.home.startSession', 'Start Session')}
              </Button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
