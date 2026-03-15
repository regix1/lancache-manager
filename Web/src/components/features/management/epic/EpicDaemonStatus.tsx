import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, Loader2, LogOut, XCircle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { EpicIcon } from '@components/ui/EpicIcon';

import { EpicAuthModal } from '@components/modals/auth/EpicAuthModal';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
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
  const [authStatus, setAuthStatus] = useState<EpicMappingAuthStatus | null>(null);
  const [hasError, setHasError] = useState(false);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);

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
    loadStatus();
  }, [loadStatus]);

  // Refresh on relevant events
  useEffect(() => {
    const handleUpdate = () => {
      loadStatus();
    };
    on('EpicGameMappingsUpdated', handleUpdate);
    on('EpicMappingProgress', handleUpdate);
    return () => {
      off('EpicGameMappingsUpdated', handleUpdate);
      off('EpicMappingProgress', handleUpdate);
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

  const isAuthenticated = authStatus?.isAuthenticated ?? false;

  return (
    <>
      <Card>
        {/* Header: Epic icon + Title + HelpPopover */}
        <div className="flex items-center gap-3 mb-4">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[var(--theme-epic-subtle)] text-[var(--theme-epic)]">
            <EpicIcon size={20} />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">
            {t('management.sections.integrations.epicDaemonStatus.title')}
          </h3>
          <HelpPopover position="left" width={320}>
            <HelpSection
              title={t(
                'management.sections.integrations.epicDaemonStatus.help.authentication.title'
              )}
              variant="subtle"
            >
              <HelpDefinition
                items={[
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
                ]}
              />
            </HelpSection>
            <HelpNote type="info">
              {t('management.sections.integrations.epicDaemonStatus.help.note')}
            </HelpNote>
          </HelpPopover>
        </div>

        {/* Error Warning */}
        {hasError && (
          <div className="p-2 mb-2 rounded-lg bg-themed-warning text-themed-warning text-xs">
            {t(
              'management.sections.integrations.epicDaemonStatus.loadError',
              'Failed to load Epic status. Displaying default values.'
            )}
          </div>
        )}

        {/* Auth Status Row */}
        <div className="p-3 rounded-lg bg-themed-tertiary">
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div className="flex-1 min-w-0">
              <p className="text-themed-primary text-sm font-medium mb-1">
                {isAuthenticated
                  ? t('management.sections.integrations.epicDaemonStatus.connectedAs', {
                      name: authStatus?.displayName || 'Epic User'
                    })
                  : t('management.sections.integrations.epicDaemonStatus.status')}
              </p>
              <p className="text-xs text-themed-muted">
                {isAuthenticated
                  ? t('management.sections.integrations.epicDaemonStatus.gamesDiscovered', {
                      count: authStatus?.gamesDiscovered || 0
                    })
                  : t('management.sections.integrations.epicDaemonStatus.notConnectedDesc')}
              </p>
            </div>
            <div className="flex-shrink-0">
              {isAuthenticated ? (
                <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-success text-themed-success">
                  <CheckCircle size={14} />
                  {t('management.sections.integrations.epicDaemonStatus.connected')}
                </span>
              ) : authMode === 'authenticated' && !mockMode ? (
                <Button onClick={handleLoginClick} variant="outline" size="sm">
                  {t('management.sections.integrations.epicDaemonStatus.loginButton')}
                </Button>
              ) : (
                <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-secondary text-themed-muted">
                  <XCircle size={14} />
                  {t('management.sections.integrations.epicDaemonStatus.notConnected')}
                </span>
              )}
            </div>
          </div>
        </div>

        {/* Authenticated: Logout */}
        {isAuthenticated && authMode === 'authenticated' && !mockMode && (
          <div className="mt-3 pt-3 border-t border-[var(--theme-border)]">
            <Button
              onClick={handleLogout}
              disabled={loggingOut}
              variant="outline"
              color="red"
              size="sm"
            >
              {loggingOut ? <Loader2 className="w-4 h-4 animate-spin" /> : <LogOut size={14} />}
              {t('management.sections.integrations.epicDaemonStatus.logout')}
            </Button>
          </div>
        )}
      </Card>

      {/* Auth Modal */}
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
