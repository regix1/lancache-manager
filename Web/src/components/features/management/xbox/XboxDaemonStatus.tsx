import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, XCircle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { XboxIcon } from '@components/ui/XboxIcon';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import XboxGameMappings from './XboxGameMappings';
import XboxMappingLoginModal from './XboxMappingLoginModal';
import type { EpicDaemonStatusDto } from '../../../../types';

// Xbox mapping is login-required (Microsoft account device-code). An admin signs in HERE - on the
// mapping admin card - to discover their library and populate the shared mapping table WITHOUT
// starting a prefill, mirroring Epic's admin-page login (EpicDaemonStatus). Because Xbox has no
// daemon-free auth client, the login reuses the prefill daemon device-code flow (XboxMappingLoginModal);
// the same login is also available on the Prefill page for users without admin access. This card
// reports daemon connectivity (Docker availability + active session count) and embeds the shared
// Xbox game library. Status is read from /api/xbox-daemon and refreshed live via the
// /xbox-prefill-daemon hub events.

interface XboxDaemonStatusProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const XboxDaemonStatus: React.FC<XboxDaemonStatusProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();
  const [status, setStatus] = useState<EpicDaemonStatusDto | null>(null);
  const [hasError, setHasError] = useState(false);
  const [showLogin, setShowLogin] = useState(false);

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
  // An authenticated daemon session means the admin is already signed in for mapping, so the login
  // control is hidden (mirroring Epic, which swaps Login for Logout when connected). The field is
  // optional on older daemon responses, so default to 0.
  const isAuthenticated = (status?.authenticatedSessions ?? 0) > 0;

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
          <div className="flex-shrink-0 flex flex-col items-start sm:items-end gap-2">
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
            {authMode === 'authenticated' && !mockMode && !isAuthenticated && (
              <Button
                onClick={() => setShowLogin(true)}
                variant="filled"
                color="green"
                size="sm"
                disabled={!isReady}
              >
                {t(
                  'management.sections.integrations.xboxDaemonStatus.loginButton',
                  'Login with Xbox'
                )}
              </Button>
            )}
          </div>
        </div>
      </div>

      {/* Game Library (aggregated across all discovery sources) - collapsible dropdown */}
      <div className="mt-4">
        <XboxGameMappings />
      </div>

      {/* Device-code login flow (mounts only while open, so the daemon hub is not connected on
          every Integrations-tab view). On success it collects the shared catalog - no prefill. */}
      {showLogin && (
        <XboxMappingLoginModal
          onClose={() => setShowLogin(false)}
          onAuthenticated={loadStatus}
          onError={onError}
          onSuccess={onSuccess}
        />
      )}
    </Card>
  );
};

export default XboxDaemonStatus;
