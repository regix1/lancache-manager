import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { XboxIcon } from '@components/ui/XboxIcon';
import DaemonStatusCard from '../daemon-status/DaemonStatusCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import type {
  XboxMappingAuthStateChangedEvent,
  XboxMappingCompleteEvent
} from '@contexts/SignalRContext/types';
import ApiService from '@services/api.service';
import { ApiError } from '@services/apiError';
import { type AuthMode } from '@services/auth.service';
import { getIntegrationReasonKey } from '../../../../types';
import XboxGameMappings from './XboxGameMappings';
import XboxMappingLoginModal from './XboxMappingLoginModal';
import { useXboxMappingAuth } from '@hooks/useXboxMappingAuth';

// Xbox mapping is login-required (Microsoft account device-code). An admin signs in HERE — on the
// mapping admin card — to discover their library and populate the shared mapping table WITHOUT
// starting a prefill, mirroring Epic's admin-page login (EpicDaemonStatus). Login is daemon-free:
// the manager hosts the MSA OAuth device-code flow directly, so Docker is NOT required to sign in.
// Status is refreshed live via mapping/auth completion and mapping-data update events.

interface XboxDaemonStatusProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const TERMINAL_AUTH_STATUSES = new Set(['completed', 'failed', 'cancelled']);

const XboxDaemonStatus: React.FC<XboxDaemonStatusProps> = ({ mockMode, onError, onSuccess }) => {
  const { t } = useTranslation();
  const { on, off, isConnected } = useSignalR();
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);

  const {
    state: loginState,
    actions: loginActions,
    startLogin,
    cancelLogin,
    authStatus,
    refreshStatus: loadStatus,
    statusLoading: loading,
    statusError: hasError,
    loginDeadline,
    identity
  } = useXboxMappingAuth({
    loginStatusNotifications: true,
    onSuccess: () => {
      setShowAuthModal(false);
      loadStatus();
      onSuccess?.(t('management.sections.integrations.xboxDaemonStatus.loginSuccess'));
    },
    onError: (message: string) => {
      console.error('Xbox mapping login error:', message);
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
    const handleMappingsUpdated = () => {
      loadStatus();
    };
    const handleMappingComplete = (_event: XboxMappingCompleteEvent) => {
      loadStatus();
    };
    const handleAuthStateChanged = (event: XboxMappingAuthStateChangedEvent) => {
      if (TERMINAL_AUTH_STATUSES.has(event.status)) loadStatus();
    };
    on('XboxGameMappingsUpdated', handleMappingsUpdated);
    on('XboxMappingComplete', handleMappingComplete);
    on('XboxMappingAuthStateChanged', handleAuthStateChanged);
    return () => {
      off('XboxGameMappingsUpdated', handleMappingsUpdated);
      off('XboxMappingComplete', handleMappingComplete);
      off('XboxMappingAuthStateChanged', handleAuthStateChanged);
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
    // Guard against a double-click: a second login-start would mint a second operationId and its own
    // terminal notification, showing the card twice. The modal being open (or a start in flight) means
    // one attempt already owns the flow.
    if (showAuthModal || loginState.loading) return;
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
      await ApiService.logoutXboxMapping();
      if (identityRef.current !== caller) return;
      await loadStatus();
      if (identityRef.current !== caller) return;
      onSuccess?.(t('management.sections.integrations.xboxDaemonStatus.logoutSuccess'));
    } catch (err) {
      if (identityRef.current !== caller) return;
      console.error('Logout failed:', err);
      onError?.(
        err instanceof ApiError && err.body?.stageKey
          ? t(err.body.stageKey, err.body.context ?? {})
          : t('management.sections.integrations.xboxDaemonStatus.logoutFailed')
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

  const loginExpiresInDays =
    authStatus?.expiresAtUtc != null
      ? Math.max(
          0,
          Math.ceil((new Date(authStatus.expiresAtUtc).getTime() - Date.now()) / 86_400_000)
        )
      : null;

  return (
    <>
      <DaemonStatusCard
        accordionId="integrations-xbox"
        title={t('management.sections.integrations.xboxDaemonStatus.title')}
        description={t('management.sections.integrations.xboxDaemonStatus.summary')}
        icon={XboxIcon}
        iconColor="--theme-xbox"
        help={{
          title: t('management.sections.integrations.xboxDaemonStatus.help.authentication.title'),
          definitions: [
            {
              term: t(
                'management.sections.integrations.xboxDaemonStatus.help.authentication.loginRequired.term'
              ),
              description: t(
                'management.sections.integrations.xboxDaemonStatus.help.authentication.loginRequired.description'
              )
            },
            {
              term: t(
                'management.sections.integrations.xboxDaemonStatus.help.authentication.gameDiscovery.term'
              ),
              description: t(
                'management.sections.integrations.xboxDaemonStatus.help.authentication.gameDiscovery.description'
              )
            }
          ],
          note: t('management.sections.integrations.xboxDaemonStatus.help.note')
        }}
        loading={loading}
        loadingMessage={t('management.sections.integrations.xboxDaemonStatus.loadingStatus')}
        hasError={hasError}
        errorMessage={t('management.sections.integrations.xboxDaemonStatus.loadError')}
        connected={isAuthenticated}
        connectedLabel={t('management.sections.integrations.xboxDaemonStatus.connected')}
        notConnectedLabel={t('management.sections.integrations.xboxDaemonStatus.notConnected')}
        headline={
          isAuthenticated
            ? t('management.sections.integrations.xboxDaemonStatus.connectedAs', {
                name: authStatus?.displayName ?? 'Xbox User',
                defaultValue: 'Connected as {{name}}'
              })
            : t('management.sections.integrations.xboxDaemonStatus.notConnected')
        }
        detail={
          isAuthenticated
            ? t('management.sections.integrations.xboxDaemonStatus.connectedDesc')
            : t('management.sections.integrations.xboxDaemonStatus.notConnectedDesc')
        }
        extraDetail={
          isAuthenticated &&
          loginExpiresInDays !== null && (
            <p className="text-xs text-themed-muted mt-1">
              {t('management.sections.integrations.xboxDaemonStatus.loginExpiresInDays', {
                count: loginExpiresInDays,
                defaultValue:
                  'Login valid for about {{count}} more days (auto-renews while running)'
              })}
            </p>
          )
        }
        auth={{
          enabled: !mockMode,
          reason,
          logoutDisabled: authStatus?.canLogout !== true,
          loginDisabled: loginState.canAuthenticate !== true,
          loginPending: loginState.loading,
          loginLabel: t('management.sections.integrations.xboxDaemonStatus.loginButton'),
          logoutLabel: t('management.sections.integrations.xboxDaemonStatus.logout'),
          onLogin: handleLoginClick,
          onLogout: handleLogout,
          loggingOut
        }}
      >
        <XboxGameMappings />
      </DaemonStatusCard>

      <XboxMappingLoginModal
        opened={showAuthModal}
        onClose={() => setShowAuthModal(false)}
        state={loginState}
        actions={loginActions}
        onCancelLogin={cancelLogin}
        loginDeadline={loginDeadline}
      />
    </>
  );
};

export default XboxDaemonStatus;
