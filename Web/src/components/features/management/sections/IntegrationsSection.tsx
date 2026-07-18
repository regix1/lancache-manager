import React, { Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
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
      <div className="space-y-8">
        {/* Steam - merged PICS authentication + Web API card */}
        <section>
          <h3 className="integrations-group-label">
            {t('management.sections.integrations.steamIntegration')}
          </h3>
          <HighlightGlow enabled={highlightSteamApi} scrollIntoView>
            <SteamIntegrationCard
              authMode={authMode}
              mockMode={mockMode}
              onError={onError}
              onSuccess={onSuccess}
            />
          </HighlightGlow>
        </section>

        {/* Epic Games - merged authentication + library card */}
        <section>
          <h3 className="integrations-group-label">
            {t('management.sections.integrations.epicIntegration')}
          </h3>
          <HighlightGlow enabled={highlightEpic} scrollIntoView>
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
          <HighlightGlow enabled={highlightBattleNet} scrollIntoView>
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
          <HighlightGlow enabled={highlightXbox} scrollIntoView>
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
        </section>
      </div>
    </div>
  );
};

export default IntegrationsSection;
