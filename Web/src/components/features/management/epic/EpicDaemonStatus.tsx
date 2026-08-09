import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { EpicIcon } from '@components/ui/EpicIcon';
import { EpicAuthModal } from '@components/modals/auth/EpicAuthModal';
import EpicGameMappings from './EpicGameMappings';
import DaemonStatusCard from '../daemon-status/DaemonStatusCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import { useEpicMappingAuth } from '@hooks/useEpicMappingAuth';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import type { EpicMappingAuthStatus } from '../../../../types';

interface EpicDaemonStatusProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const EpicDaemonStatus: React.FC<EpicDaemonStatusProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();
  // Authentication now flows through the unified activity registry, which is authoritative once ready.
  // NOT an `||`: a scheduled catalog refresh whose token renewal fails calls SetIsAuthenticated(false)
  // without emitting EpicGameMappingsUpdated/EpicMappingProgress (see EpicMappingService.Scheduling.cs),
  // so a stale cached authStatus.isAuthenticated=true would otherwise mask that correct registry false.
  const activity = useActivityStatus();
  const [authStatus, setAuthStatus] = useState<EpicMappingAuthStatus | null>(null);
  const [hasError, setHasError] = useState(false);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);
  const [loading, setLoading] = useState(true);

  const loadStatus = useCallback(async () => {
    try {
      const auth = await ApiService.getEpicMappingAuthStatus();
      setAuthStatus(auth);
    } catch {
      setHasError(true);
      setAuthStatus({
        isAuthenticated: false,
        displayName: null,
        lastCollectionUtc: null,
        gamesDiscovered: 0
      });
    }
  }, []);

  useEffect(() => {
    loadStatus().finally(() => setLoading(false));
  }, [loadStatus]);

  // Refresh on relevant events
  useEffect(() => {
    const handleUpdate = () => {
      loadStatus();
    };
    on('EpicGameMappingsUpdated', handleUpdate);
    on('EpicMappingProgress', handleUpdate);
    on('EpicMappingComplete', handleUpdate);
    return () => {
      off('EpicGameMappingsUpdated', handleUpdate);
      off('EpicMappingProgress', handleUpdate);
      off('EpicMappingComplete', handleUpdate);
    };
  }, [on, off, loadStatus]);

  // Refresh data when SignalR reconnects (catches events missed during disconnect)
  useEffect(() => {
    if (connectionState === 'connected') {
      loadStatus();
    }
  }, [connectionState, loadStatus]);

  const {
    state: loginState,
    actions: loginActions,
    startLogin
  } = useEpicMappingAuth({
    loginStatusNotifications: true,
    onSuccess: () => {
      setShowAuthModal(false);
      loadStatus();
      onSuccess?.('Epic Games authentication successful.');
    },
    onError: (message: string) => {
      console.error('Epic mapping login error:', message);
      onError?.(message);
    }
  });

  const handleLoginClick = async () => {
    setShowAuthModal(true);
    await startLogin();
  };

  const handleLogout = async () => {
    setLoggingOut(true);
    try {
      await ApiService.logoutEpicMapping();
      await loadStatus();
      onSuccess?.('Logged out of Epic Games.');
    } catch (err) {
      console.error('Logout failed:', err);
      onError?.('Failed to logout from Epic Games.');
    } finally {
      setLoggingOut(false);
    }
  };

  const isAuthenticated = activity.isActiveOrFallback(
    'integration',
    'epic',
    'authenticated',
    authStatus?.isAuthenticated ?? false
  );

  return (
    <>
      <DaemonStatusCard
        accordionId="integrations-epic"
        title={t('management.sections.integrations.epicDaemonStatus.title')}
        description={t('management.sections.integrations.epicDaemonStatus.summary')}
        icon={EpicIcon}
        iconColor="var(--theme-epic)"
        help={{
          title: t('management.sections.integrations.epicDaemonStatus.help.authentication.title'),
          definitions: [
            {
              term: t(
                'management.sections.integrations.epicDaemonStatus.help.authentication.loginRequired.term'
              ),
              description: t(
                'management.sections.integrations.epicDaemonStatus.help.authentication.loginRequired.description'
              )
            },
            {
              term: t(
                'management.sections.integrations.epicDaemonStatus.help.authentication.gameDiscovery.term'
              ),
              description: t(
                'management.sections.integrations.epicDaemonStatus.help.authentication.gameDiscovery.description'
              )
            }
          ],
          note: t('management.sections.integrations.epicDaemonStatus.help.note')
        }}
        loading={loading}
        loadingMessage={t('management.sections.integrations.epicDaemonStatus.loadingStatus')}
        hasError={hasError}
        errorMessage={t('management.sections.integrations.epicDaemonStatus.loadError')}
        connected={isAuthenticated}
        connectedLabel={t('management.sections.integrations.epicDaemonStatus.connected')}
        notConnectedLabel={t('management.sections.integrations.epicDaemonStatus.notConnected')}
        headline={
          isAuthenticated
            ? t('management.sections.integrations.epicDaemonStatus.connectedAs', {
                name: authStatus?.displayName || 'Epic User'
              })
            : t('management.sections.integrations.epicDaemonStatus.notConnected')
        }
        detail={
          isAuthenticated
            ? t('management.sections.integrations.epicDaemonStatus.connectedDesc')
            : t('management.sections.integrations.epicDaemonStatus.notConnectedDesc')
        }
        auth={{
          enabled: authMode === 'authenticated' && !mockMode,
          loginLabel: t('management.sections.integrations.epicDaemonStatus.loginButton'),
          logoutLabel: t('management.sections.integrations.epicDaemonStatus.logout'),
          onLogin: handleLoginClick,
          onLogout: handleLogout,
          loggingOut
        }}
      >
        <EpicGameMappings />
      </DaemonStatusCard>

      <EpicAuthModal
        opened={showAuthModal}
        onClose={() => setShowAuthModal(false)}
        state={loginState}
        actions={loginActions}
      />
    </>
  );
};

export default EpicDaemonStatus;
