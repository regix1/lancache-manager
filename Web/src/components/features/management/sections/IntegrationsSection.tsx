import React, { Suspense } from 'react';
import { Card } from '@components/ui/Card';
import { type AuthMode } from '@services/auth.service';
import SteamLoginManager from '../steam/SteamLoginManager';
import SteamWebApiStatus from '../steam/SteamWebApiStatus';
import GrafanaEndpoints from '../grafana/GrafanaEndpoints';

interface IntegrationsSectionProps {
  authMode: AuthMode;
  steamAuthMode: 'anonymous' | 'authenticated';
  mockMode: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
}

const IntegrationsSection: React.FC<IntegrationsSectionProps> = ({
  authMode,
  steamAuthMode,
  mockMode,
  onError,
  onSuccess
}) => {
  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-integrations"
      aria-labelledby="tab-integrations"
    >
      {/* Section Header */}
      <div className="mb-6">
        <h2 className="text-xl font-semibold text-themed-primary mb-1">
          Integration & Services
        </h2>
        <p className="text-themed-secondary text-sm">
          Connect to Steam, configure Grafana endpoints, and manage external service integrations
        </p>
      </div>

      {/* Content Grid - Two column layout for larger screens */}
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        {/* Steam Integration Column */}
        <div className="space-y-4">
          <div className="flex items-center gap-2 mb-2">
            <div
              className="w-1 h-5 rounded-full"
              style={{ backgroundColor: 'var(--theme-steam)' }}
            />
            <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
              Steam Integration
            </h3>
          </div>

          <SteamLoginManager
            authMode={authMode}
            mockMode={mockMode}
            onError={onError}
            onSuccess={onSuccess}
          />

          <SteamWebApiStatus steamAuthMode={steamAuthMode} />
        </div>

        {/* Monitoring & Metrics Column */}
        <div className="space-y-4">
          <div className="flex items-center gap-2 mb-2">
            <div
              className="w-1 h-5 rounded-full"
              style={{ backgroundColor: 'var(--theme-primary)' }}
            />
            <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
              Monitoring & Metrics
            </h3>
          </div>

          <Suspense
            fallback={
              <Card>
                <div className="flex items-center justify-center py-8">
                  <div className="text-themed-muted">Loading endpoints...</div>
                </div>
              </Card>
            }
          >
            <GrafanaEndpoints />
          </Suspense>
        </div>
      </div>
    </div>
  );
};

export default IntegrationsSection;
