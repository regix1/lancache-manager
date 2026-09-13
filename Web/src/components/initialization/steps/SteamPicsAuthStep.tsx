import React, { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Shield, Users, User } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { SelectableCard } from '@components/ui/SelectableCard';
import { StepHeader } from '@components/initialization/StepHeader';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { useSteamAuthentication } from '@hooks/useSteamAuthentication';
import ApiService from '@services/api.service';
import { ApiError } from '@services/apiError';
import { useSteamAuth } from '@contexts/useSteamAuth';
import { useAuth } from '@contexts/useAuth';
import { integrationReasonKeys } from '../../../types';

interface SteamPicsAuthStepProps {
  onComplete: (usingSteamAuth: boolean) => void;
}

type AuthMode = 'anonymous' | 'account';

export const SteamPicsAuthStep: React.FC<SteamPicsAuthStepProps> = ({ onComplete }) => {
  const { t } = useTranslation();
  const { access, refreshSteamAuth } = useSteamAuth();
  const { authenticationEnabled, authMode, accountId, sessionId } = useAuth();
  const identity = JSON.stringify([authenticationEnabled, authMode, accountId, sessionId]);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const [selectedMode, setSelectedMode] = useState<AuthMode>('anonymous');
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    setShowAuthModal(false);
    setError(null);
    setSaving(false);
  }, [identity]);
  const canUseAnonymous = access?.canManage === true && access.canCancel !== true;
  // No onError handler on purpose. The modal keeps the reason and stays open, the same as every
  // other login surface: closing it here ran resetAuthForm, which nulls the error, so the sentence
  // explaining the refusal was thrown away before anyone could read it and the wizard dropped back
  // on the anonymous card saying nothing at all.
  const { state, actions, loginDeadline } = useSteamAuthentication({
    autoStartPics: false,
    onSuccess: () => {
      setShowAuthModal(false);
      onComplete(true);
    }
  });

  const handleModeSelect = (mode: AuthMode) => {
    if (mode === 'anonymous' ? !canUseAnonymous : state.canAuthenticate === false) return;
    setSelectedMode(mode);
    setError(null);
    if (mode === 'account') {
      setShowAuthModal(true);
    }
  };

  const handleContinueAnonymous = async () => {
    if (identityRef.current !== identity || !canUseAnonymous || saving) return;
    const caller = identity;
    setSaving(true);
    setError(null);

    try {
      await ApiService.setSteamAuthMode('anonymous');
      if (identityRef.current !== caller) return;
      await refreshSteamAuth();
      if (identityRef.current !== caller) return;
      onComplete(false);
    } catch (err: unknown) {
      if (identityRef.current !== caller) return;
      setError(
        err instanceof ApiError && err.body?.stageKey
          ? t(err.body.stageKey, err.body.context ?? {})
          : t('initialization.steamPicsAuth.networkError')
      );
    } finally {
      if (identityRef.current === caller) setSaving(false);
    }
  };

  const handleCloseModal = () => {
    if (!state.loading) {
      setShowAuthModal(false);
      actions.resetAuthForm();
      setSelectedMode('anonymous');
    }
  };

  // The wizard runs the same in-process sign-in as the Management tab, so dismissing it has to
  // reach the server the same way. The phone-approval poll outlives the request that started it,
  // and until it gives up the account is marked as signing in and every later attempt is refused.
  // Best-effort: the poll ends on its own window if this request fails.
  const handleCancelLogin = () => {
    actions.cancelLogin?.();
  };

  return (
    <>
      <div className="space-y-5">
        {(!access || access.ownershipReason) && (
          <p className="text-sm text-themed-muted" role="status">
            {t(
              integrationReasonKeys[access?.ownershipReason ?? ''] ??
                'errors.integration.statusUnavailable'
            )}
          </p>
        )}
        <StepHeader
          icon={<Shield className="w-7 h-7 icon-info" />}
          iconBackground="bg-themed-info"
          title={t('initialization.steamPicsAuth.title')}
          description={t('initialization.steamPicsAuth.subtitle')}
        />

        {/* Info Box */}
        <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
          <p className="text-themed-secondary">
            <strong className="text-themed-primary">
              {t('initialization.steamPicsAuth.whatIsDepotMapping')}
            </strong>{' '}
            {t('initialization.steamPicsAuth.depotMappingDesc')}
          </p>
        </div>

        {/* Mode Selection Cards */}
        <fieldset className="grid gap-3">
          <legend className="sr-only">{t('initialization.steamPicsAuth.title')}</legend>
          <SelectableCard
            name="steam-auth-mode"
            value="anonymous"
            disabled={!canUseAnonymous || saving}
            checked={selectedMode === 'anonymous'}
            onChange={() => handleModeSelect('anonymous')}
            icon={<Users className="icon-primary" />}
            title={t('initialization.steamPicsAuth.anonymousMode')}
            description={t('initialization.steamPicsAuth.anonymousModeDesc')}
          />
          <SelectableCard
            name="steam-auth-mode"
            value="account"
            disabled={state.canAuthenticate === false || saving}
            checked={selectedMode === 'account'}
            onChange={() => handleModeSelect('account')}
            icon={<User className="icon-success" />}
            title={t('initialization.steamPicsAuth.accountMode')}
            description={t('initialization.steamPicsAuth.accountModeDesc')}
          />
        </fieldset>

        {/* Error Display */}
        {error && <Alert color="error">{error}</Alert>}

        {/* Continue Button */}
        {selectedMode === 'anonymous' && (
          <Button
            variant="filled"
            color="primary"
            onClick={handleContinueAnonymous}
            loading={saving}
            disabled={!canUseAnonymous || saving}
            fullWidth
          >
            {saving
              ? t('initialization.steamPicsAuth.saving')
              : t('initialization.steamPicsAuth.continueAnonymous')}
          </Button>
        )}
      </div>

      {/* Authentication Modal */}
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
