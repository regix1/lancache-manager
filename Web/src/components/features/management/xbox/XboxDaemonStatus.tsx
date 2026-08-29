import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { XboxIcon } from '@components/ui/XboxIcon';
import DaemonStatusCard from '../daemon-status/DaemonStatusCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import type {
  XboxMappingAuthStateChangedEvent,
  XboxMappingCompleteEvent
} from '@contexts/SignalRContext/types';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import type { XboxMappingAuthStatus } from '../../../../types';
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

const XboxDaemonStatus: React.FC<XboxDaemonStatusProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { on, off, isConnected } = useSignalR();
  // Authentication now flows through the unified activity registry, which is authoritative once ready -
  // trusting a stale cached authStatus over a fresh registry false is exactly the bug found in Epic's
  // scheduled-refresh path (EpicDaemonStatus.tsx), so this stays consistent rather than an `||`.
  const activity = useActivityStatus();
  const [authStatus, setAuthStatus] = useState<XboxMappingAuthStatus | null>(null);
  const [hasError, setHasError] = useState(false);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);
  const [loading, setLoading] = useState(!mockMode);

  const loadStatus = useCallback(async () => {
    // Demo/mock mode has no admin session, and auth-status is AdminOnly, so a fetch would 401/403
    // and permanently error the card. Surface a clean empty status instead of hitting the endpoint.
    if (mockMode) {
      setAuthStatus({
        isAuthenticated: false,
        displayName: null,
        lastCollectionUtc: null,
        gamesDiscovered: 0,
        loginInProgress: false,
        expiresAtUtc: null
      });
      setHasError(false);
      return;
    }
    try {
      const auth = await ApiService.getXboxMappingAuthStatus();
      setAuthStatus(auth);
      setHasError(false);
    } catch {
      setHasError(true);
      setAuthStatus({
        isAuthenticated: false,
        displayName: null,
        lastCollectionUtc: null,
        gamesDiscovered: 0,
        loginInProgress: false,
        expiresAtUtc: null
      });
    }
  }, [mockMode]);

  useEffect(() => {
    loadStatus().finally(() => setLoading(false));
  }, [loadStatus]);

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

  const {
    state: loginState,
    actions: loginActions,
    startLogin,
    cancelLogin
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

  const handleLoginClick = async () => {
    // Guard against a double-click: a second login-start would mint a second operationId and its own
    // terminal notification, showing the card twice. The modal being open (or a start in flight) means
    // one attempt already owns the flow.
    if (showAuthModal || loginState.loading) return;
    setShowAuthModal(true);
    await startLogin();
  };

  const handleLogout = async () => {
    setLoggingOut(true);
    try {
      await ApiService.logoutXboxMapping();
      await loadStatus();
      onSuccess?.(t('management.sections.integrations.xboxDaemonStatus.logoutSuccess'));
    } catch (err) {
      console.error('Logout failed:', err);
      onError?.(t('management.sections.integrations.xboxDaemonStatus.logoutFailed'));
    } finally {
      setLoggingOut(false);
    }
  };

  const isAuthenticated = activity.isActiveOrFallback(
    'integration',
    'xbox',
    'authenticated',
    authStatus?.isAuthenticated ?? false
  );

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
          enabled: authMode === 'authenticated' && !mockMode,
          loginLabel: t('management.sections.integrations.xboxDaemonStatus.loginButton'),
          logoutLabel: t('management.sections.integrations.xboxDaemonStatus.logout'),
          onLogin: handleLoginClick,
          onLogout: handleLogout,
          loggingOut,
          loginPending: loginState.loading,
          loginDisabled: showAuthModal || loginState.loading
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
      />
    </>
  );
};

export default XboxDaemonStatus;
