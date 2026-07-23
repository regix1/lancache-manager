import React, { Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import HighlightGlow from '@components/ui/HighlightGlow';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { type AuthMode } from '@services/auth.service';
import SteamIntegrationCard from '../steam/SteamIntegrationCard';
import GrafanaEndpoints from '../grafana/GrafanaEndpoints';
import EpicDaemonStatus from '../epic/EpicDaemonStatus';
import BattleNetDaemonStatus from '../battlenet/BattleNetDaemonStatus';
import RiotDaemonStatus from '../riot/RiotDaemonStatus';
import XboxDaemonStatus from '../xbox/XboxDaemonStatus';

interface IntegrationsSectionProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
  highlightSteamApi?: boolean;
  highlightEpic?: boolean;
  highlightBattleNet?: boolean;
  highlightXbox?: boolean;
}

const IntegrationsSection: React.FC<IntegrationsSectionProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess,
  highlightSteamApi,
  highlightEpic,
  highlightBattleNet,
  highlightXbox
}) => {
  const { t } = useTranslation();

  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-integrations"
      aria-labelledby="tab-integrations"
    >
      {/* Steam - merged PICS authentication + Web API */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center justify-between gap-2 mb-3 sm:mb-4">
          <div className="flex items-center gap-2 min-w-0">
            <div className="w-1 h-5 rounded-full bg-[var(--theme-steam)]" />
            <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
              {t('management.sections.integrations.steamIntegration')}
            </h3>
          </div>
          <AccordionGroupToggle />
        </div>
        <HighlightGlow enabled={highlightSteamApi} scrollIntoView>
          <SteamIntegrationCard
            authMode={authMode}
            mockMode={mockMode}
            onError={onError}
            onSuccess={onSuccess}
          />
        </HighlightGlow>
      </div>

      {/* Epic Games - merged authentication + library */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-epic)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.integrations.epicIntegration')}
          </h3>
        </div>
        <HighlightGlow enabled={highlightEpic} scrollIntoView>
          <EpicDaemonStatus
            authMode={authMode}
            mockMode={mockMode}
            onError={onError}
            onSuccess={onSuccess}
          />
        </HighlightGlow>
      </div>

      {/* Battle.net - anonymous prefill daemon status (no account login) */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-blizzard)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.integrations.battlenetIntegration', 'Battle.net Integration')}
          </h3>
        </div>
        <HighlightGlow enabled={highlightBattleNet} scrollIntoView>
          <BattleNetDaemonStatus onError={onError} />
        </HighlightGlow>
      </div>

      {/* Riot - anonymous prefill daemon status (no account login) */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-riot)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.integrations.riotIntegration', 'Riot Integration')}
          </h3>
        </div>
        <RiotDaemonStatus onError={onError} />
      </div>

      {/* Xbox - login-required mapping status */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-xbox)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.integrations.xboxIntegration', 'Xbox Integration')}
          </h3>
        </div>
        <HighlightGlow enabled={highlightXbox} scrollIntoView>
          <XboxDaemonStatus
            authMode={authMode}
            mockMode={mockMode}
            onError={onError}
            onSuccess={onSuccess}
          />
        </HighlightGlow>
      </div>

      {/* Monitoring - Grafana endpoints (last group: no bottom margin) */}
      <div>
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-blue)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.integrations.monitoringMetrics')}
          </h3>
        </div>
        <Suspense
          fallback={
            <Card>
              <div className="flex items-center justify-center gap-2 py-8">
                <LoadingSpinner size="md" />
                <span className="text-themed-muted">
                  {t('management.sections.integrations.loadingEndpoints')}
                </span>
              </div>
            </Card>
          }
        >
          <GrafanaEndpoints />
        </Suspense>
      </div>
    </div>
  );
};

export default IntegrationsSection;
