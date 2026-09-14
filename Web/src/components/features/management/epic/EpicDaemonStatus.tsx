import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { EpicIcon } from '@components/ui/EpicIcon';
import { EpicAuthModal } from '@components/modals/auth/EpicAuthModal';
import EpicGameMappings from './EpicGameMappings';
import DaemonStatusCard from '../daemon-status/DaemonStatusCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useEpicMappingAuth } from '@hooks/useEpicMappingAuth';
import ApiService from '@services/api.service';
import { ApiError } from '@services/apiError';
import { type AuthMode } from '@services/auth.service';
import { getIntegrationReasonKey } from '../../../../types';

interface EpicDaemonStatusProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const EpicDaemonStatus: React.FC<EpicDaemonStatusProps> = ({ mockMode, onError, onSuccess }) => {
  const { t } = useTranslation();
  const { on, off, isConnected } = useSignalR();
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);

  const {
    state: loginState,
    actions: loginActions,
    startLogin,
    authStatus,
    refreshStatus: loadStatus,
    statusLoading: loading,
    statusError: hasError,
    loginDeadline,
    identity
  } = useEpicMappingAuth({
    onSuccess: () => {
      setShowAuthModal(false);
      loadStatus();
      onSuccess?.(t('management.sections.integrations.epicDaemonStatus.authSuccess'));
    },
    onError: (message: string) => {
      console.error('Epic mapping login error:', message);
      onError?.(message);
    }
  });

  const identityRef = useRef(identity);
  identityRef.current = identity;
  useEffect(() => {
    setShowAuthModal(false);
    setLoggingOut(false);
  }, [identity]);

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
  useReconnectRefetch(isConnected, loadStatus);

  const handleLoginClick = async () => {
    if (
      mockMode ||
      (authStatus?.canSignIn !== true && authStatus?.canRecover !== true && !loginState.attemptId)
    )
      return;
    setShowAuthModal(true);
    await startLogin();
  };

  const handleLogout = async () => {
    if (
      identityRef.current !== identity ||
      mockMode ||
      authStatus?.canLogout !== true ||
      loggingOut
    )
      return;
    const caller = identity;
    setLoggingOut(true);
    try {
      await ApiService.logoutEpicMapping();
      if (identityRef.current !== caller) return;
      await loadStatus();
      if (identityRef.current !== caller) return;
      onSuccess?.(t('management.sections.integrations.epicDaemonStatus.logoutSuccess'));
    } catch (err) {
      if (identityRef.current !== caller) return;
      console.error('Logout failed:', err);
      onError?.(
        err instanceof ApiError && err.body?.stageKey
          ? t(err.body.stageKey, err.body.context ?? {})
          : t('management.sections.integrations.epicDaemonStatus.logoutError')
      );
    } finally {
      if (identityRef.current === caller) setLoggingOut(false);
    }
  };

  const isAuthenticated = authStatus?.canManage === true && authStatus.isAuthenticated;
  const reason = authStatus?.ownershipReason
    ? t(getIntegrationReasonKey(authStatus.ownershipReason))
    : authStatus
      ? null
      : t('errors.integration.statusUnavailable');

  return (
    <>
      <DaemonStatusCard
        accordionId="integrations-epic"
        title={t('management.sections.integrations.epicDaemonStatus.title')}
        description={t('management.sections.integrations.epicDaemonStatus.summary')}
        icon={EpicIcon}
        iconColor="--theme-epic"
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
                name:
                  authStatus?.displayName ||
                  t('management.sections.integrations.epicDaemonStatus.defaultUserName')
              })
            : t('management.sections.integrations.epicDaemonStatus.notConnected')
        }
        detail={
          isAuthenticated
            ? t('management.sections.integrations.epicDaemonStatus.connectedDesc')
            : t('management.sections.integrations.epicDaemonStatus.notConnectedDesc')
        }
        auth={{
          enabled: !mockMode,
          reason,
          logoutDisabled: authStatus?.canLogout !== true,
          loginDisabled: loginState.canAuthenticate !== true,
          loginPending: loginState.loading,
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
        loginDeadline={loginDeadline}
        opened={showAuthModal}
        onClose={() => setShowAuthModal(false)}
        state={loginState}
        actions={loginActions}
      />
    </>
  );
};

export default EpicDaemonStatus;
