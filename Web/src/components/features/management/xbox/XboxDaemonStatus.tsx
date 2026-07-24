import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, XCircle } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { XboxIcon } from '@components/ui/XboxIcon';
import { LoadingState } from '@components/ui/ManagerCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import type { XboxMappingProgressEvent } from '@contexts/SignalRContext/types';
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
// Status is refreshed live via XboxMappingProgress and XboxGameMappingsUpdated SignalR events.

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
  // Authentication now flows through the unified activity registry, which is authoritative once ready -
  // trusting a stale cached authStatus over a fresh registry false is exactly the bug found in Epic's
  // scheduled-refresh path (EpicDaemonStatus.tsx), so this stays consistent rather than an `||`.
  const activity = useActivityStatus();
  const [authStatus, setAuthStatus] = useState<XboxMappingAuthStatus | null>(null);
  const [hasError, setHasError] = useState(false);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);
  const [loading, setLoading] = useState(true);
  const [expanded, setExpanded] = useState(false);
  useAccordionGroupItem('integrations-xbox', expanded, () => setExpanded((prev) => !prev));

  const loadStatus = useCallback(async () => {
    // Demo/mock mode has no admin session, and auth-status is AdminOnly, so a fetch would 401/403
    // and permanently error the card. Surface a clean empty status instead of hitting the endpoint.
    if (mockMode) {
      setAuthStatus({
        isAuthenticated: false,
        displayName: null,
        lastCollectionUtc: null,
        gamesDiscovered: 0,
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
    // Only the terminal login event changes auth status; interim 10%/40% ticks would redundantly
    // re-fetch the AdminOnly auth-status endpoint.
    const handleProgress = (event: XboxMappingProgressEvent) => {
      if (event.isTerminal) {
        loadStatus();
      }
    };
    on('XboxGameMappingsUpdated', handleMappingsUpdated);
    on('XboxMappingProgress', handleProgress);
    return () => {
      off('XboxGameMappingsUpdated', handleMappingsUpdated);
      off('XboxMappingProgress', handleProgress);
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
    startLogin,
    cancelLogin
  } = useXboxMappingAuth({
    onSuccess: () => {
      setShowAuthModal(false);
      loadStatus();
      onSuccess?.('Xbox authentication successful.');
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
      onSuccess?.('Logged out of Xbox.');
    } catch (err) {
      console.error('Logout failed:', err);
      onError?.('Failed to logout from Xbox.');
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

  const statusBadge = !loading ? (
    isAuthenticated ? (
      <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-success text-themed-success">
        <CheckCircle size={14} />
        {t('management.sections.integrations.xboxDaemonStatus.connected', 'Connected')}
      </span>
    ) : (
      <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-secondary text-themed-muted">
        <XCircle size={14} />
        {t('management.sections.integrations.xboxDaemonStatus.notConnected', 'Not Connected')}
      </span>
    )
  ) : undefined;

  const helpAccessory = (
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
                'Xbox requires a Microsoft account login to discover your game library. Sign-in uses a device code entered in your own browser. No password ever enters the server.'
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
          'Sign in to enable Xbox game discovery. Docker is not required. Authentication runs directly in the manager.'
        )}
      </HelpNote>
    </HelpPopover>
  );

  return (
    <>
      <AccordionSection
        title={t('management.sections.integrations.xboxDaemonStatus.title', 'Xbox')}
        description={t('management.sections.integrations.xboxDaemonStatus.summary')}
        titleAccessory={helpAccessory}
        icon={XboxIcon}
        iconColor="var(--theme-xbox)"
        isExpanded={expanded}
        onToggle={() => setExpanded((prev) => !prev)}
        badge={statusBadge}
      >
        {loading ? (
          <LoadingState
            message={t(
              'management.sections.integrations.xboxDaemonStatus.loadingStatus',
              'Loading Xbox status...'
            )}
            rows={1}
          />
        ) : (
          <>
            {hasError && (
              <div className="p-2 mb-2 rounded-lg bg-themed-warning text-themed-warning text-xs">
                {t(
                  'management.sections.integrations.xboxDaemonStatus.loadError',
                  'Failed to load Xbox status. Displaying default values.'
                )}
              </div>
            )}

            <div className="p-3 rounded-lg bg-themed-tertiary">
              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                <div className="flex-1 min-w-0">
                  <p className="text-themed-primary text-sm font-medium mb-1">
                    {isAuthenticated
                      ? t('management.sections.integrations.xboxDaemonStatus.connectedAs', {
                          name: authStatus?.displayName ?? 'Xbox User',
                          defaultValue: 'Connected as {{name}}'
                        })
                      : t(
                          'management.sections.integrations.xboxDaemonStatus.notConnected',
                          'Not Connected'
                        )}
                  </p>
                  <p className="text-xs text-themed-muted">
                    {isAuthenticated
                      ? t(
                          'management.sections.integrations.xboxDaemonStatus.connectedDesc',
                          'Library synced. Game detection is active.'
                        )
                      : t(
                          'management.sections.integrations.xboxDaemonStatus.notConnectedDesc',
                          'Sign in with your Microsoft account to enable Xbox game discovery.'
                        )}
                  </p>
                  {isAuthenticated && loginExpiresInDays !== null && (
                    <p className="text-xs text-themed-muted mt-1">
                      {t('management.sections.integrations.xboxDaemonStatus.loginExpiresInDays', {
                        count: loginExpiresInDays,
                        defaultValue:
                          'Login valid for about {{count}} more days (auto-renews while running)'
                      })}
                    </p>
                  )}
                </div>
                {authMode === 'authenticated' && !mockMode && (
                  <div className="flex-shrink-0">
                    {isAuthenticated ? (
                      <Button
                        onClick={handleLogout}
                        loading={loggingOut}
                        variant="filled"
                        color="red"
                        size="sm"
                      >
                        {t('management.sections.integrations.xboxDaemonStatus.logout', 'Logout')}
                      </Button>
                    ) : (
                      <Button
                        onClick={handleLoginClick}
                        loading={loginState.loading}
                        disabled={showAuthModal || loginState.loading}
                        variant="filled"
                        color="blue"
                        size="sm"
                      >
                        {t(
                          'management.sections.integrations.xboxDaemonStatus.loginButton',
                          'Login with Xbox'
                        )}
                      </Button>
                    )}
                  </div>
                )}
              </div>
            </div>
          </>
        )}

        <div className="mt-4">
          <XboxGameMappings />
        </div>
      </AccordionSection>

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
