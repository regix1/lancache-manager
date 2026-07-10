import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, AlertTriangle } from 'lucide-react';

import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { useSteamAuthentication } from '@hooks/useSteamAuthentication';
import { useSteamAuth } from '@contexts/useSteamAuth';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { storage } from '@utils/storage';
import { getErrorMessage } from '@utils/error';

interface SteamLoginManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const SteamLoginManager: React.FC<SteamLoginManagerProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess
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
    loginStatusNotifications: true,
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
      onError?.(t('common.fullAuthRequired'));
      return;
    }

    setLoading(true);
    try {
      const response = await fetch(
        '/api/steam-auth',
        ApiService.getFetchOptions({
          method: 'DELETE'
        })
      );

      if (response.ok) {
        // Update context directly - no need to refresh from backend
        // The backend has already cleared the Steam auth, just update local state
        setContextSteamAuthMode('anonymous');
        setContextUsername('');
        onSuccess?.(t('management.steamAuth.switchedToAnonymous'));
      } else {
        const errorBody = await response.json();
        onError?.(errorBody?.message || t('modals.steamAuth.errors.failedToSwitchToAnonymous'));
      }
    } catch (err: unknown) {
      onError?.(getErrorMessage(err) || t('modals.steamAuth.errors.failedToSwitchToAnonymous'));
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
    {
      value: 'anonymous',
      label: t('management.steamAuth.anonymous'),
      description: t('management.steamAuth.status.publicOnly')
    },
    {
      value: 'authenticated',
      label: t('management.steamAuth.accountLogin'),
      description: t('management.steamAuth.status.canAccessRestricted')
    }
  ];

  return (
    <>
      <div>
        <div className="flex items-center gap-2 mb-3">
          <h4 className="text-sm font-semibold text-themed-primary">
            {t('management.steamAuth.sectionTitle')}
          </h4>
          <HelpPopover position="left" width={320}>
            <HelpSection title={t('management.steamAuth.help.authModes.title')} variant="subtle">
              <HelpDefinition
                items={[
                  {
                    term: t('management.steamAuth.help.authModes.anonymous.term'),
                    description: t('management.steamAuth.help.authModes.anonymous.description')
                  },
                  {
                    term: t('management.steamAuth.help.authModes.accountLogin.term'),
                    description: t('management.steamAuth.help.authModes.accountLogin.description')
                  }
                ]}
              />
            </HelpSection>

            <HelpSection title={t('management.steamAuth.help.depotMapping.title')} variant="subtle">
              <HelpDefinition
                items={[
                  {
                    term: t('management.steamAuth.help.depotMapping.automatic.term'),
                    description: t('management.steamAuth.help.depotMapping.automatic.description')
                  },
                  {
                    term: t('management.steamAuth.help.depotMapping.manual.term'),
                    description: t('management.steamAuth.help.depotMapping.manual.description')
                  }
                ]}
              />
            </HelpSection>

            <HelpNote type="info">{t('management.steamAuth.help.note')}</HelpNote>
          </HelpPopover>
        </div>

        {/* Auto-logout warning banner */}
        {autoLogoutMessage && (
          <Alert color="red" className="mb-4" icon={<AlertTriangle className="w-5 h-5" />}>
            <div className="flex items-start gap-3">
              <div className="flex-1">
                <p className="font-medium text-sm mb-1">
                  {t('management.steamAuth.autoLogout.title')}
                </p>
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
              <p className="font-medium text-sm mb-1">
                {t('management.steamAuth.prefillWarning.title')}
              </p>
              <p className="text-xs opacity-90">
                {t('management.steamAuth.prefillWarning.description')}
              </p>
            </div>
          </Alert>
        )}

        {/* Main auth mode selector */}
        <div className="space-y-2">
          <div className="p-3 rounded-lg bg-themed-tertiary">
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
                    dropdownWidth="w-72"
                    alignRight={true}
                  />
                </div>
              ) : (
                <div className="w-full sm:w-auto sm:min-w-[180px] px-3 py-2 rounded-lg text-center bg-themed-secondary border border-themed-primary">
                  <p className="text-sm text-themed-muted">
                    {steamAuthMode === 'authenticated'
                      ? t('management.steamAuth.accountLogin')
                      : t('management.steamAuth.anonymous')}
                  </p>
                </div>
              )}
            </div>
          </div>

          {/* Configuration section */}
          <div className="p-3 rounded-lg bg-themed-tertiary">
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
              <SegmentedControl
                size="sm"
                value={autoStartPics ? 'automatic' : 'manual'}
                onChange={(value) => handleAutoStartPicsChange(value === 'automatic')}
                options={[
                  {
                    value: 'automatic',
                    label: t('management.steamAuth.automatic'),
                    disabled: loading || mockMode
                  },
                  {
                    value: 'manual',
                    label: t('management.steamAuth.manual'),
                    disabled: loading || mockMode
                  }
                ]}
              />
            </div>
          </div>

          {/* Authenticated status row */}
          {steamAuthMode === 'authenticated' && (
            <div className="p-3 rounded-lg bg-themed-tertiary">
              <div className="flex items-center justify-between gap-4">
                <div className="flex-1 min-w-0">
                  <p className="text-themed-primary text-sm font-medium">
                    {t('management.steamAuth.authenticatedAs')}{' '}
                    <strong>{authenticatedUsername || t('management.steamAuth.steamUser')}</strong>
                  </p>
                </div>
                <div className="flex-shrink-0">
                  {authMode === 'authenticated' && !mockMode ? (
                    <Button
                      onClick={handleSwitchToAnonymous}
                      loading={loading}
                      variant="filled"
                      color="red"
                      size="sm"
                    >
                      {t('management.steamAuth.logout')}
                    </Button>
                  ) : (
                    <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-success text-themed-success">
                      <CheckCircle size={14} />
                      {t('management.steamAuth.connected')}
                    </span>
                  )}
                </div>
              </div>
            </div>
          )}
        </div>
      </div>

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
