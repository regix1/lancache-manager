import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { User, UserCheck } from 'lucide-react';

import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
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

  const { state, actions, loginDeadline } = useSteamAuthentication({
    autoStartPics,
    loginStatusNotifications: true,
    onSuccess: (message) => {
      setContextSteamAuthMode('authenticated');
      setShowAuthModal(false);
      refreshSteamAuth();
      onSuccess?.(message);
    }
  });

  useEffect(() => {
    const savedPref = storage.getItem('autoStartPics');
    if (savedPref !== null) {
      setAutoStartPics(savedPref === 'true');
    }
  }, []);

  const handleAutoStartPicsChange = (enabled: boolean) => {
    setAutoStartPics(enabled);
    storage.setItem('autoStartPics', enabled.toString());
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

  // Dismissing the modal is a decision to stop, so tell the server. The credentials poll outlives
  // the request that started it - closing the browser tab must never kill a confirmation the user
  // has already approved on their phone, but pressing Cancel has to. Best-effort: the poll gives up
  // on its own window if this request fails, and the form is already reset either way.
  const handleCancelLogin = () => {
    void ApiService.cancelSteamLogin().catch((err: unknown) => {
      console.error('Cancel Steam login failed:', getErrorMessage(err));
    });
  };

  const canManage = authMode === 'authenticated' && !mockMode;
  const isAuthenticated = steamAuthMode === 'authenticated';

  return (
    <>
      <div className="steam-integration">
        <div className="steam-integration__subhead">
          <h4 className="mgmt-subhead caps-label">{t('management.steamAuth.sectionTitle')}</h4>
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

            <HelpNote type="warning">
              {t('management.steamAuth.prefillWarning.description')}
            </HelpNote>

            <HelpNote type="info">{t('management.steamAuth.help.note')}</HelpNote>
          </HelpPopover>
        </div>

        {autoLogoutMessage && (
          <Alert
            color="red"
            title={t('management.steamAuth.autoLogout.title')}
            withCloseButton
            onClose={clearAutoLogoutMessage}
          >
            {autoLogoutMessage}
          </Alert>
        )}

        <div className="mgmt-list">
          <div className="mgmt-row">
            <div
              className={`icon-box icon-box--sm steam-integration__account-icon${
                isAuthenticated ? ' steam-integration__account-icon--on' : ''
              }`}
            >
              {isAuthenticated ? <UserCheck className="w-4 h-4" /> : <User className="w-4 h-4" />}
            </div>
            <div className="mgmt-row__body">
              <p className="mgmt-row__title">
                {isAuthenticated
                  ? authenticatedUsername || t('management.steamAuth.steamUser')
                  : t('management.steamAuth.status.anonymous')}
              </p>
              <p className="mgmt-row__meta">
                {isAuthenticated
                  ? t('management.steamAuth.status.canAccessRestricted')
                  : t('management.steamAuth.status.publicOnly')}
              </p>
            </div>
            <div className="mgmt-row__actions">
              {canManage ? (
                isAuthenticated ? (
                  <Button
                    onClick={handleSwitchToAnonymous}
                    loading={loading}
                    variant="filled"
                    color="secondary"
                    size="sm"
                    stableWidth
                  >
                    {t('management.steamAuth.logout')}
                  </Button>
                ) : (
                  <Button
                    onClick={() => setShowAuthModal(true)}
                    variant="filled"
                    color="primary"
                    size="sm"
                    disabled={loading}
                  >
                    {t('management.steamAuth.accountLogin')}
                  </Button>
                )
              ) : null}
            </div>
          </div>

          <div className="mgmt-row">
            <div className="mgmt-row__body">
              <p className="mgmt-row__title">{t('management.steamAuth.depotMappingAfterLogin')}</p>
              <p className="mgmt-row__meta">
                {autoStartPics
                  ? t('management.steamAuth.autoRebuild')
                  : t('management.steamAuth.manualRebuild')}
              </p>
            </div>
            <div className="mgmt-row__actions">
              <SegmentedControl
                size="sm"
                fullWidth
                className="steam-integration__segments"
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
        </div>
      </div>

      <SteamAuthModal
        opened={showAuthModal}
        onClose={handleCloseModal}
        state={state}
        actions={actions}
        onCancelLogin={handleCancelLogin}
        loginDeadline={loginDeadline}
      />
    </>
  );
};

export default SteamLoginManager;
