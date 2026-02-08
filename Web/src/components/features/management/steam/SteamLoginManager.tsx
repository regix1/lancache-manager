import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Key, User, AlertTriangle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import HighlightGlow from '@components/ui/HighlightGlow';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { useSteamAuthentication } from '@hooks/useSteamAuthentication';
import { useSteamAuth } from '@contexts/SteamAuthContext';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { storage } from '@utils/storage';

interface SteamLoginManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  highlight?: boolean;
}

const SteamLoginManager: React.FC<SteamLoginManagerProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess,
  highlight
}) => {
  const { t } = useTranslation();
  const {
    steamAuthMode,
    username: authenticatedUsername,
    autoLogoutMessage,
    refreshSteamAuth,
    setSteamAuthMode: setContextSteamAuthMode,
    setUsername: setContextUsername,
    clearAutoLogoutMessage
  } = useSteamAuth();
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loading, setLoading] = useState(false);
  const [autoStartPics, setAutoStartPics] = useState<boolean>(false);

  const { state, actions } = useSteamAuthentication({
    autoStartPics,
    onSuccess: (message) => {
      setContextSteamAuthMode('authenticated');
      setShowAuthModal(false);
      refreshSteamAuth(); // Refresh to get the authenticated username
      onSuccess?.(message);
    }
  });

  useEffect(() => {
    // Load auto-start preference from localStorage
    const savedPref = storage.getItem('autoStartPics');
    if (savedPref !== null) {
      setAutoStartPics(savedPref === 'true');
    }
  }, []);

  const handleAutoStartPicsChange = (enabled: boolean) => {
    setAutoStartPics(enabled);
    storage.setItem('autoStartPics', enabled.toString());
  };

  const handleModeChange = (newMode: string) => {
    if (newMode === 'authenticated' && steamAuthMode === 'anonymous') {
      // Show auth modal when switching to authenticated
      setShowAuthModal(true);
    } else if (newMode === 'anonymous' && steamAuthMode === 'authenticated') {
      // Switch back to anonymous
      handleSwitchToAnonymous();
    }
  };

  const handleSwitchToAnonymous = async () => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setLoading(true);
    try {
      const response = await fetch('/api/steam-auth', ApiService.getFetchOptions({
        method: 'DELETE'
      }));

      if (response.ok) {
        // Update context directly - no need to refresh from backend
        // The backend has already cleared the Steam auth, just update local state
        setContextSteamAuthMode('anonymous');
        setContextUsername('');
        onSuccess?.('Switched to anonymous Steam mode. Depot mappings preserved.');
      } else {
        const error = await response.json();
        onError?.(error.message || t('modals.steamAuth.errors.failedToSwitchToAnonymous'));
      }
    } catch (err: unknown) {
      onError?.((err instanceof Error ? err.message : String(err)) || t('modals.steamAuth.errors.failedToSwitchToAnonymous'));
    } finally {
      setLoading(false);
    }
  };

  const handleCloseModal = () => {
    if (!state.loading) {
      setShowAuthModal(false);
      actions.resetAuthForm();
    }
  };

  const dropdownOptions = [
    { value: 'anonymous', label: t('management.steamAuth.modes.anonymous') },
    { value: 'authenticated', label: t('management.steamAuth.modes.authenticated') }
  ];

  return (
    <>
      <HighlightGlow enabled={highlight}>
        <Card>
        <div className="flex items-center gap-3 mb-6">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-blue">
            <Key className="w-5 h-5 icon-blue" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">{t('management.steamAuth.title')}</h3>
          <HelpPopover position="left" width={320}>
            <HelpSection title={t('management.steamAuth.help.authModes.title')}>
              <div className="space-y-1.5">
                <HelpDefinition term={t('management.steamAuth.help.authModes.anonymous.term')} termColor="blue">
                  {t('management.steamAuth.help.authModes.anonymous.description')}
                </HelpDefinition>
                <HelpDefinition term={t('management.steamAuth.help.authModes.accountLogin.term')} termColor="green">
                  {t('management.steamAuth.help.authModes.accountLogin.description')}
                </HelpDefinition>
              </div>
            </HelpSection>

            <HelpSection title={t('management.steamAuth.help.depotMapping.title')} variant="subtle">
              {t('management.steamAuth.help.depotMapping.description')}
            </HelpSection>

            <HelpNote type="info">
              {t('management.steamAuth.help.note')}
            </HelpNote>
          </HelpPopover>
        </div>

        {/* Auto-logout warning banner */}
        {autoLogoutMessage && (
          <Alert color="red" className="mb-4" icon={<AlertTriangle className="w-5 h-5" />}>
            <div className="flex items-start gap-3">
              <div className="flex-1">
                <p className="font-medium text-sm mb-1">{t('management.steamAuth.autoLogout.title')}</p>
                <p className="text-xs opacity-90">{autoLogoutMessage}</p>
              </div>
              <Button
                size="xs"
                variant="filled"
                onClick={clearAutoLogoutMessage}
                className="bg-white/20 text-themed-button border-none hover:!bg-white/30"
              >
                {t('common.dismiss')}
              </Button>
            </div>
          </Alert>
        )}

        {/* Prefill session warning banner */}
        {steamAuthMode === 'authenticated' && (
          <Alert color="yellow" className="mb-4" icon={<AlertTriangle className="w-5 h-5" />}>
            <div>
              <p className="font-medium text-sm mb-1">Prefill requires separate login</p>
              <p className="text-xs opacity-90">
                Auto-login is not available for prefill sessions. Steam requires each connection to authenticate with its own credentials — sharing a session token causes one connection to be disconnected. Each prefill session must be logged in manually.
              </p>
            </div>
          </Alert>
        )}

        {/* Main auth mode selector */}
        <div className={`p-4 rounded-lg mb-4 bg-themed-tertiary `}>
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div className="flex-1 min-w-0">
              <p className="text-themed-primary text-sm font-medium mb-1">
                {steamAuthMode === 'authenticated'
                  ? t('management.steamAuth.status.loggedIn')
                  : t('management.steamAuth.status.anonymous')}
              </p>
              <p className="text-xs text-themed-muted">
                {steamAuthMode === 'authenticated'
                  ? t('management.steamAuth.status.canAccessRestricted')
                  : t('management.steamAuth.status.publicOnly')}
              </p>
            </div>

            {authMode === 'authenticated' && !mockMode ? (
              <div className="w-full sm:w-auto sm:min-w-[220px]">
                <EnhancedDropdown
                  options={dropdownOptions}
                  value={steamAuthMode}
                  onChange={handleModeChange}
                  disabled={loading}
                />
              </div>
            ) : (
              <div className="w-full sm:w-auto sm:min-w-[180px] px-3 py-2 rounded-lg text-center bg-themed-secondary border border-themed-primary">
                <p className="text-sm text-themed-muted">
                  {steamAuthMode === 'authenticated' ? t('management.steamAuth.accountLogin') : t('management.steamAuth.anonymous')}
                </p>
              </div>
            )}
          </div>
        </div>

        {/* Configuration section */}
        <div className={`p-4 rounded-lg bg-themed-tertiary `}>
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
            <div className="flex-1 min-w-0">
              <p className="text-themed-primary font-medium text-sm mb-1">
                {t('management.steamAuth.depotMappingAfterLogin')}
              </p>
              <p className="text-xs text-themed-muted">
                {autoStartPics
                  ? t('management.steamAuth.autoRebuild')
                  : t('management.steamAuth.manualRebuild')}
              </p>
            </div>
            <div className="inline-flex rounded-lg p-0.5 bg-themed-secondary">
              <button
                onClick={() => handleAutoStartPicsChange(true)}
                disabled={loading || mockMode}
                className={`px-3 py-1.5 text-sm font-medium rounded-md transition-all ${
                  loading || mockMode ? 'opacity-50 cursor-not-allowed' : ''
                } ${autoStartPics ? 'toggle-btn-active' : 'toggle-btn-inactive'}`}
              >
                {t('management.steamAuth.automatic')}
              </button>
              <button
                onClick={() => handleAutoStartPicsChange(false)}
                disabled={loading || mockMode}
                className={`px-3 py-1.5 text-sm font-medium rounded-md transition-all ${
                  loading || mockMode ? 'opacity-50 cursor-not-allowed' : ''
                } ${!autoStartPics ? 'toggle-btn-active' : 'toggle-btn-inactive'}`}
              >
                {t('management.steamAuth.manual')}
              </button>
            </div>
          </div>
        </div>

        {/* Authenticated status */}
        {steamAuthMode === 'authenticated' && (
          <div className="mt-4">
            <Alert color="green">
              <div className="flex items-center justify-between">
                <span className="text-sm">
                  <User className="w-4 h-4 inline mr-2" />
                  {t('management.steamAuth.authenticatedAs')} <strong>{authenticatedUsername || t('management.steamAuth.steamUser')}</strong>
                </span>
                <Button
                  size="xs"
                  variant="filled"
                  color="red"
                  onClick={handleSwitchToAnonymous}
                  disabled={loading || mockMode}
                >
                  {t('management.steamAuth.logout')}
                </Button>
              </div>
            </Alert>
          </div>
        )}
        </Card>
      </HighlightGlow>

      {/* Authentication Modal */}
      <SteamAuthModal
        opened={showAuthModal}
        onClose={handleCloseModal}
        state={state}
        actions={actions}
      />
    </>
  );
};

export default SteamLoginManager;
