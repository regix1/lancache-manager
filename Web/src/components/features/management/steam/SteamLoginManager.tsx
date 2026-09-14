import React, { useState, useEffect, useRef } from 'react';
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
import { ApiError } from '@services/apiError';
import { useAuth } from '@contexts/useAuth';
import { getIntegrationReasonKey } from '../../../../types';

interface SteamLoginManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const SteamLoginManager: React.FC<SteamLoginManagerProps> = ({ mockMode, onError, onSuccess }) => {
  const { t } = useTranslation();
  const {
    steamAuthMode,
    access,
    username: authenticatedUsername,
    autoLogoutMessage,
    refreshSteamAuth,
    clearAutoLogoutMessage
  } = useSteamAuth();
  const { authenticationEnabled, accountId, sessionId, authMode } = useAuth();
  const identity = JSON.stringify([authenticationEnabled, accountId, sessionId, authMode]);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [loading, setLoading] = useState(false);
  const [autoStartPics, setAutoStartPics] = useState<boolean>(false);
  useEffect(() => {
    setShowAuthModal(false);
    setLoading(false);
  }, [identity]);

  const { state, actions, loginDeadline } = useSteamAuthentication({
    autoStartPics,
    onSuccess: (message) => {
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
    if (loading || mockMode || (access?.canSignIn !== true && access?.canRecover !== true)) return;
    setAutoStartPics(enabled);
    storage.setItem('autoStartPics', enabled.toString());
  };

  const handleSwitchToAnonymous = async () => {
    if (identityRef.current !== identity || loading || mockMode || access?.canLogout !== true)
      return;

    setLoading(true);
    const caller = identity;
    try {
      await ApiService.clearSteamAuth();
      if (identityRef.current !== caller) return;
      await refreshSteamAuth();
      if (identityRef.current !== caller) return;
      onSuccess?.(t('management.steamAuth.switchedToAnonymous'));
    } catch (err: unknown) {
      if (identityRef.current !== caller) return;
      onError?.(
        err instanceof ApiError && err.body?.stageKey
          ? t(err.body.stageKey, err.body.context ?? {})
          : t('modals.steamAuth.errors.failedToSwitchToAnonymous')
      );
    } finally {
      if (identityRef.current === caller) {
        setLoading(false);
        void refreshSteamAuth();
      }
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
    actions.cancelLogin?.();
  };

  const canManage = !mockMode && access?.canManage === true;
  const canSignIn = !mockMode && (access?.canSignIn === true || access?.canRecover === true);
  const reason = !access
    ? t('errors.integration.statusUnavailable')
    : access.ownershipReason
      ? t(getIntegrationReasonKey(access.ownershipReason))
      : null;
  const isAuthenticated = steamAuthMode === 'authenticated';

  return (
    <>
      <div className="steam-integration">
        {reason && <p className="text-sm text-themed-secondary">{reason}</p>}
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
            <div className="mgmt-row__actions steam-integration__pair">
              {isAuthenticated || access?.canLogout || access?.ownershipReason ? (
                <>
                  <Button
                    onClick={() => {
                      if (canSignIn) setShowAuthModal(true);
                    }}
                    variant="filled"
                    color="primary"
                    size="sm"
                    disabled={loading || !canSignIn}
                  >
                    {t('management.steamAuth.signInAgain')}
                  </Button>
                  <Button
                    onClick={handleSwitchToAnonymous}
                    loading={loading}
                    variant="filled"
                    color="secondary"
                    size="sm"
                    stableWidth
                    disabled={!canManage || access?.canLogout !== true}
                  >
                    {t('management.steamAuth.logout')}
                  </Button>
                </>
              ) : (
                <Button
                  onClick={() => {
                    if (canSignIn) setShowAuthModal(true);
                  }}
                  variant="filled"
                  color="primary"
                  size="sm"
                  disabled={loading || !canSignIn}
                >
                  {t('management.steamAuth.accountLogin')}
                </Button>
              )}
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
                    disabled: loading || !canSignIn
                  },
                  {
                    value: 'manual',
                    label: t('management.steamAuth.manual'),
                    disabled: loading || !canSignIn
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
