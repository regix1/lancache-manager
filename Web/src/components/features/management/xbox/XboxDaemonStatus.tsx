import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, XCircle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { XboxIcon } from '@components/ui/XboxIcon';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import XboxGameMappings from './XboxGameMappings';
import type { EpicDaemonStatusDto } from '../../../../types';

// Xbox prefill is login-required (Microsoft account device-code), but the login flow lives on the
// PREFILL page (XboxAuthModal), not here. This card reports daemon connectivity (Docker
// availability + active session count) and embeds the shared Xbox game library, mirroring the
// daemon-status cards for the other prefill services. Status is read from /api/xbox-daemon and
// refreshed live via the /xbox-prefill-daemon hub events.

interface XboxDaemonStatusProps {
  onError?: (message: string) => void;
}

const XboxDaemonStatus: React.FC<XboxDaemonStatusProps> = ({ onError }) => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();
  const [status, setStatus] = useState<EpicDaemonStatusDto | null>(null);
  const [hasError, setHasError] = useState(false);

  const loadStatus = useCallback(async () => {
    try {
      const data = await ApiService.getXboxDaemonStatus();
      setStatus(data);
      setHasError(false);
    } catch {
      setHasError(true);
      setStatus({
        dockerAvailable: false,
        activeSessions: 0,
        maxSessionsPerUser: 1,
        sessionTimeoutMinutes: 120
      });
      onError?.(
        t(
          'management.sections.integrations.xboxDaemonStatus.loadError',
          'Failed to load Xbox status. Displaying default values.'
        )
      );
    }
  }, [onError, t]);

  useEffect(() => {
    loadStatus();
  }, [loadStatus]);

  // Refresh when the daemon reports a status change over the Xbox hub
  useEffect(() => {
    const handleUpdate = () => {
      loadStatus();
    };
    on('XboxStatusChanged', handleUpdate);
    on('XboxDaemonSessionCreated', handleUpdate);
    on('XboxDaemonSessionTerminated', handleUpdate);
    return () => {
      off('XboxStatusChanged', handleUpdate);
      off('XboxDaemonSessionCreated', handleUpdate);
      off('XboxDaemonSessionTerminated', handleUpdate);
    };
  }, [on, off, loadStatus]);

  // Refresh data when SignalR reconnects (catches events missed during disconnect)
  useEffect(() => {
    if (connectionState === 'connected') {
      loadStatus();
    }
  }, [connectionState, loadStatus]);

  const isReady = status?.dockerAvailable ?? false;
  const activeSessions = status?.activeSessions ?? 0;

  return (
    <Card>
      {/* Header: Xbox icon + Title + HelpPopover */}
      <div className="flex items-center gap-3 mb-4">
        <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[var(--theme-xbox-subtle)] text-[var(--theme-xbox)]">
          <XboxIcon size={20} />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary">
          {t('management.sections.integrations.xboxDaemonStatus.title', 'Xbox')}
        </h3>
        <HelpPopover position="left" width={320}>
          <HelpSection
            title={t(
              'management.sections.integrations.xboxDaemonStatus.help.authentication.title',
              'Xbox Authentication'
            )}
            variant="subtle"
          >
            <HelpDefinition
              items={[
                {
                  term: t(
                    'management.sections.integrations.xboxDaemonStatus.help.authentication.loginRequired.term',
                    'Login Required'
                  ),
                  description: t(
                    'management.sections.integrations.xboxDaemonStatus.help.authentication.loginRequired.description',
                    'Xbox requires a Microsoft account login to discover your game library. Sign-in uses a device code entered in your own browser, so no password ever enters the container.'
                  )
                },
                {
                  term: t(
                    'management.sections.integrations.xboxDaemonStatus.help.authentication.gameDiscovery.term',
                    'Game Discovery'
                  ),
                  description: t(
                    'management.sections.integrations.xboxDaemonStatus.help.authentication.gameDiscovery.description',
                    'Once connected, your Xbox and Microsoft Store library is scanned to identify cached downloads and match them to game titles.'
                  )
                }
              ]}
            />
          </HelpSection>
          <HelpNote type="info">
            {t(
              'management.sections.integrations.xboxDaemonStatus.help.note',
              'Sign in on the Prefill page. The daemon container needs Docker to be available to run prefill sessions.'
            )}
          </HelpNote>
        </HelpPopover>
        <div className="ml-auto flex-shrink-0">
          {isReady ? (
            <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-success text-themed-success">
              <CheckCircle size={14} />
              {t('management.sections.integrations.xboxDaemonStatus.connected', 'Connected')}
            </span>
          ) : (
            <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-secondary text-themed-muted">
              <XCircle size={14} />
              {t('management.sections.integrations.xboxDaemonStatus.notConnected', 'Not Connected')}
            </span>
          )}
        </div>
      </div>

      {/* Error Warning */}
      {hasError && (
        <div className="p-2 mb-2 rounded-lg bg-themed-warning text-themed-warning text-xs">
          {t(
            'management.sections.integrations.xboxDaemonStatus.loadError',
            'Failed to load Xbox status. Displaying default values.'
          )}
        </div>
      )}

      {/* Status Row */}
      <div className="p-3 rounded-lg bg-themed-tertiary">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
          <div className="flex-1 min-w-0">
            <p className="text-themed-primary text-sm font-medium mb-1">
              {isReady
                ? t(
                    'management.sections.integrations.xboxDaemonStatus.dockerStatus',
                    'Docker Service'
                  )
                : t(
                    'management.sections.integrations.xboxDaemonStatus.notConnected',
                    'Not Connected'
                  )}
            </p>
            <p className="text-xs text-themed-muted">
              {isReady
                ? t(
                    'management.sections.integrations.xboxDaemonStatus.dockerAvailableDesc',
                    'Docker is available and ready for Xbox prefill sessions.'
                  )
                : t(
                    'management.sections.integrations.xboxDaemonStatus.dockerUnavailableDesc',
                    'Start Docker Desktop to enable Xbox authentication.'
                  )}
            </p>
          </div>
          <div className="flex-shrink-0">
            <span className="text-xs text-themed-muted">
              {activeSessions > 0
                ? t('management.sections.integrations.xboxDaemonStatus.activeSessions', {
                    count: activeSessions,
                    defaultValue: '{{count}} active session'
                  })
                : t(
                    'management.sections.integrations.xboxDaemonStatus.noActiveSessions',
                    'No active sessions'
                  )}
            </span>
          </div>
        </div>
      </div>

      {/* Game Library (aggregated across all discovery sources) - collapsible dropdown */}
      <div className="mt-4">
        <XboxGameMappings />
      </div>
    </Card>
  );
};

export default XboxDaemonStatus;
