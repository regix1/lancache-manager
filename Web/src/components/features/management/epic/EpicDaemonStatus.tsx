import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, Loader2, LogOut, XCircle, Activity, User, Container } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { EpicIcon } from '@components/ui/EpicIcon';

import { EpicAuthModal } from '@components/modals/auth/EpicAuthModal';
import { useSignalR } from '@contexts/SignalRContext';
import { useEpicMappingAuth } from '@hooks/useEpicMappingAuth';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import type { EpicMappingAuthStatus, EpicDaemonStatusDto } from '../../../../types';

interface EpicDaemonStatusProps {
  authMode: AuthMode;
}

const EpicDaemonStatus: React.FC<EpicDaemonStatusProps> = ({ authMode }) => {
  const { t } = useTranslation();
  const { on, off } = useSignalR();
  const [authStatus, setAuthStatus] = useState<EpicMappingAuthStatus | null>(null);
  const [daemonStatus, setDaemonStatus] = useState<EpicDaemonStatusDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [hasError, setHasError] = useState(false);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);

  const loadStatus = useCallback(async () => {
    try {
      const [auth, daemon] = await Promise.all([
        ApiService.getEpicMappingAuthStatus(),
        ApiService.getEpicDaemonStatus()
      ]);
      setAuthStatus(auth);
      setDaemonStatus(daemon);
    } catch {
      // Endpoints may not exist yet - default to safe values
      setHasError(true);
      setAuthStatus({
        isAuthenticated: false,
        displayName: null,
        lastCollectionUtc: null,
        gamesDiscovered: 0
      });
      setDaemonStatus({
        dockerAvailable: false,
        activeSessions: 0,
        maxSessionsPerUser: 1,
        sessionTimeoutMinutes: 120
      });
    }

    setLoading(false);
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
    on('EpicDaemonSessionCreated', handleUpdate);
    on('EpicDaemonSessionUpdated', handleUpdate);
    on('EpicDaemonSessionTerminated', handleUpdate);
    on('EpicSessionEnded', handleUpdate);
    return () => {
      off('EpicGameMappingsUpdated', handleUpdate);
      off('EpicDaemonSessionCreated', handleUpdate);
      off('EpicDaemonSessionUpdated', handleUpdate);
      off('EpicDaemonSessionTerminated', handleUpdate);
      off('EpicSessionEnded', handleUpdate);
    };
  }, [on, off, loadStatus]);

  const {
    state: loginState,
    actions: loginActions,
    startLogin
  } = useEpicMappingAuth({
    onSuccess: () => {
      setShowAuthModal(false);
      loadStatus();
    },
    onError: (message: string) => {
      console.error('Epic mapping login error:', message);
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
    } catch (err) {
      console.error('Logout failed:', err);
    } finally {
      setLoggingOut(false);
    }
  };

  if (loading) {
    return (
      <Card>
        <div className="flex items-center justify-center py-8">
          <Loader2 className="w-5 h-5 animate-spin text-themed-secondary" />
        </div>
      </Card>
    );
  }

  const isAuthenticated = authStatus?.isAuthenticated ?? false;
  const isDockerAvailable = daemonStatus?.dockerAvailable ?? false;
  const activeSessions = daemonStatus?.activeSessions ?? 0;

  return (
    <>
      <Card>
        <div className="flex flex-col">
          {/* Header */}
          <div className="flex items-center gap-3 mb-4">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[color-mix(in_srgb,var(--theme-epic)_15%,transparent)] text-[var(--theme-epic)]">
              <EpicIcon size={20} />
            </div>
            <h3 className="text-lg font-semibold text-themed-primary">
              {t('management.sections.integrations.epicDaemonStatus.title')}
            </h3>
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
          <div className="flex items-center justify-between gap-4 p-3 rounded-lg bg-themed-tertiary">
            <div className="flex-1 min-w-0">
              <p className="text-themed-primary text-sm font-medium">
                {t('management.sections.integrations.epicDaemonStatus.status')}
              </p>
              <p className="text-xs text-themed-muted">
                {isAuthenticated
                  ? t('management.sections.integrations.epicDaemonStatus.connectedAs', {
                      name: authStatus?.displayName || 'Epic User'
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
              ) : (
                <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-secondary text-themed-muted">
                  <XCircle size={14} />
                  {t('management.sections.integrations.epicDaemonStatus.notConnected')}
                </span>
              )}
            </div>
          </div>

          {/* Docker Status Row */}
          <div className="flex items-center justify-between gap-4 p-3 mt-2 rounded-lg bg-themed-tertiary">
            <div className="flex-1 min-w-0">
              <p className="text-themed-primary text-sm font-medium">
                {t('management.sections.integrations.epicDaemonStatus.dockerStatus')}
              </p>
              <p className="text-xs text-themed-muted">
                {isDockerAvailable
                  ? t('management.sections.integrations.epicDaemonStatus.dockerAvailableDesc')
                  : t('management.sections.integrations.epicDaemonStatus.dockerUnavailableDesc')}
              </p>
            </div>
            <div className="flex-shrink-0">
              {isDockerAvailable ? (
                <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-success text-themed-success">
                  <Container size={14} />
                  {t('management.sections.integrations.epicDaemonStatus.ready')}
                </span>
              ) : (
                <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-secondary text-themed-muted">
                  <XCircle size={14} />
                  {t('management.sections.integrations.epicDaemonStatus.unavailable')}
                </span>
              )}
            </div>
          </div>

          {/* Active Prefill Sessions Row */}
          {isDockerAvailable && (
            <div
              className={`flex items-center gap-3 p-3 mt-2 rounded-lg ${
                activeSessions > 0
                  ? 'bg-[color-mix(in_srgb,var(--theme-epic)_8%,transparent)]'
                  : 'bg-themed-tertiary'
              }`}
            >
              <div className="flex-shrink-0 flex items-center justify-center">
                {activeSessions > 0 ? (
                  <Activity size={16} className="text-[var(--theme-epic)] animate-pulse" />
                ) : (
                  <Activity size={16} className="text-themed-muted" />
                )}
              </div>
              <div className="flex-1 min-w-0">
                <p className="text-themed-primary text-sm font-medium">
                  {t('management.sections.integrations.epicDaemonStatus.prefillSessions')}
                </p>
                <p className="text-xs text-themed-muted">
                  {activeSessions > 0
                    ? t('management.sections.integrations.epicDaemonStatus.activeSessions', {
                        count: activeSessions
                      })
                    : t('management.sections.integrations.epicDaemonStatus.noActiveSessions')}
                </p>
              </div>
              {activeSessions > 0 && (
                <div className="flex-shrink-0">
                  <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-semibold bg-[color-mix(in_srgb,var(--theme-epic)_15%,transparent)] text-[var(--theme-epic)]">
                    <User size={12} />
                    {activeSessions}
                  </span>
                </div>
              )}
            </div>
          )}

          {/* Authenticated - Show stats and logout */}
          {isAuthenticated && (
            <div className="mt-3 pt-3 border-t border-[var(--theme-border)]">
              <p className="text-xs text-themed-muted mb-2">
                {t('management.sections.integrations.epicDaemonStatus.gamesDiscovered', {
                  count: authStatus?.gamesDiscovered || 0
                })}
              </p>
              {authMode === 'authenticated' && (
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
              )}
            </div>
          )}

          {/* Not Authenticated - Show login button */}
          {!isAuthenticated && authMode === 'authenticated' && (
            <div className="mt-3 pt-3 border-t border-[var(--theme-border)]">
              <p className="text-xs text-themed-muted mb-2">
                {t('management.sections.integrations.epicDaemonStatus.loginDesc')}
              </p>
              <Button onClick={handleLoginClick} variant="outline" size="sm">
                {t('management.sections.integrations.epicDaemonStatus.loginButton')}
              </Button>
            </div>
          )}
        </div>
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
