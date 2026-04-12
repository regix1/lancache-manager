import React, { Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import HighlightGlow from '@components/ui/HighlightGlow';
import { type AuthMode } from '@services/auth.service';
import SteamLoginManager from '../steam/SteamLoginManager';
import SteamWebApiStatus from '../steam/SteamWebApiStatus';
import GrafanaEndpoints from '../grafana/GrafanaEndpoints';
import EpicDaemonStatus from '../epic/EpicDaemonStatus';
import EpicGameMappings from '../epic/EpicGameMappings';

interface IntegrationsSectionProps {
  authMode: AuthMode;
  steamAuthMode: 'anonymous' | 'authenticated';
  mockMode: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
  highlightSteamApi?: boolean;
  highlightEpic?: boolean;
}

const IntegrationsSection: React.FC<IntegrationsSectionProps> = ({
  authMode,
  steamAuthMode,
  mockMode,
  onError,
  onSuccess,
  highlightSteamApi,
  highlightEpic
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

      <div className="space-y-6">
        {/* Steam Integration - side by side grid */}
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

        {/* Epic Integration - stacked (auth is compact, library needs full width) */}
        <HighlightGlow enabled={highlightEpic}>
          <div className="space-y-4">
            <EpicDaemonStatus
              authMode={authMode}
              mockMode={mockMode}
              onError={onError}
              onSuccess={onSuccess}
            />
            <EpicGameMappings />
          </div>
        </HighlightGlow>

        {/* Grafana - monitoring */}
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
      </div>
    </div>
  );
};

export default IntegrationsSection;
