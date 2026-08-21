import React, { Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import { GroupHeading } from '@components/ui/GroupHeading';
import { TabPanel } from '@components/features/management/TabPanel';
import HighlightGlow from '@components/ui/HighlightGlow';
import { LoadingState } from '@components/ui/ManagerCard';
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
    <TabPanel tabId="integrations">
      {/* Steam - merged PICS authentication + Web API */}
      <div className="mb-6 sm:mb-8">
        <GroupHeading
          label={t('management.sections.integrations.steamIntegration')}
          accent="steam"
          actions={<AccordionGroupToggle />}
        />
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
        <GroupHeading label={t('management.sections.integrations.epicIntegration')} accent="epic" />
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
        <GroupHeading
          label={t('management.sections.integrations.battlenetIntegration')}
          accent="blizzard"
        />
        <HighlightGlow enabled={highlightBattleNet} scrollIntoView>
          <BattleNetDaemonStatus onError={onError} />
        </HighlightGlow>
      </div>

      {/* Riot - anonymous prefill daemon status (no account login) */}
      <div className="mb-6 sm:mb-8">
        <GroupHeading label={t('management.sections.integrations.riotIntegration')} accent="riot" />
        <RiotDaemonStatus onError={onError} />
      </div>

      {/* Xbox - login-required mapping status */}
      <div className="mb-6 sm:mb-8">
        <GroupHeading label={t('management.sections.integrations.xboxIntegration')} accent="xbox" />
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
        <GroupHeading label={t('management.sections.integrations.monitoringMetrics')} />
        <Suspense
          fallback={
            <Card>
              <LoadingState
                message={t('management.sections.integrations.loadingEndpoints')}
                shape="rows"
                rows={2}
              />
            </Card>
          }
        >
          <GrafanaEndpoints />
        </Suspense>
      </div>
    </TabPanel>
  );
};

export default IntegrationsSection;
