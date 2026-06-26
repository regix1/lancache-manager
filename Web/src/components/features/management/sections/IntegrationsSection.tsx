import React, { Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import HighlightGlow from '@components/ui/HighlightGlow';
import { type AuthMode } from '@services/auth.service';
import SteamLoginManager from '../steam/SteamLoginManager';
import SteamWebApiStatus from '../steam/SteamWebApiStatus';
import GrafanaEndpoints from '../grafana/GrafanaEndpoints';
import EpicDaemonStatus from '../epic/EpicDaemonStatus';
import BattleNetDaemonStatus from '../battlenet/BattleNetDaemonStatus';
import RiotDaemonStatus from '../riot/RiotDaemonStatus';
import XboxDaemonStatus from '../xbox/XboxDaemonStatus';

interface IntegrationsSectionProps {
  authMode: AuthMode;
  steamAuthMode: 'anonymous' | 'authenticated';
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
  steamAuthMode,
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
      {/* Page Header */}
      <div className="mb-6">
        <h2 className="text-xl font-semibold text-themed-primary mb-1">
          {t('management.sections.integrations.title')}
        </h2>
        <p className="text-themed-secondary text-sm">
          {t('management.sections.integrations.subtitle')}
        </p>
      </div>

      <div className="space-y-8">
        {/* Steam - PICS auth + Web API status side by side */}
        <section>
          <h3 className="integrations-group-label">
            {t('management.sections.integrations.steamIntegration')}
          </h3>
          <HighlightGlow enabled={highlightSteamApi}>
            <div className="integrations-grid">
              <SteamLoginManager
                authMode={authMode}
                mockMode={mockMode}
                onError={onError}
                onSuccess={onSuccess}
              />
              <SteamWebApiStatus steamAuthMode={steamAuthMode} />
            </div>
          </HighlightGlow>
        </section>

        {/* Epic Games - merged authentication + library card */}
        <section>
          <h3 className="integrations-group-label">
            {t('management.sections.integrations.epicIntegration')}
          </h3>
          <HighlightGlow enabled={highlightEpic}>
            <EpicDaemonStatus
              authMode={authMode}
              mockMode={mockMode}
              onError={onError}
              onSuccess={onSuccess}
            />
          </HighlightGlow>
        </section>

        {/* Battle.net - anonymous prefill daemon status (no account login) */}
        <section>
          <h3 className="integrations-group-label">
            {t('management.sections.integrations.battlenetIntegration', 'Battle.net Integration')}
          </h3>
          <HighlightGlow enabled={highlightBattleNet}>
            <BattleNetDaemonStatus onError={onError} />
          </HighlightGlow>
        </section>

        {/* Riot - anonymous prefill daemon status (no account login) */}
        <section>
          <h3 className="integrations-group-label">
            {t('management.sections.integrations.riotIntegration', 'Riot Integration')}
          </h3>
          <RiotDaemonStatus onError={onError} />
        </section>

        {/* Xbox - login-required mapping status (admin signs in here via device-code, no prefill) */}
        <section>
          <h3 className="integrations-group-label">
            {t('management.sections.integrations.xboxIntegration', 'Xbox Integration')}
          </h3>
          <HighlightGlow enabled={highlightXbox}>
            <XboxDaemonStatus
              authMode={authMode}
              mockMode={mockMode}
              onError={onError}
              onSuccess={onSuccess}
            />
          </HighlightGlow>
        </section>

        {/* Monitoring - Grafana endpoints */}
        <section>
          <h3 className="integrations-group-label">
            {t('management.sections.integrations.monitoringMetrics')}
          </h3>
          <Suspense
            fallback={
              <Card>
                <div className="flex items-center justify-center py-8">
                  <div className="text-themed-muted">
                    {t('management.sections.integrations.loadingEndpoints')}
                  </div>
                </div>
              </Card>
            }
          >
            <GrafanaEndpoints />
          </Suspense>
        </section>
      </div>
    </div>
  );
};

export default IntegrationsSection;
