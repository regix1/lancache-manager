import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AccessSetup } from '@components/initialization/AccessSetup';
import { ActionMenuItem } from '@components/ui/ActionMenu';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { HelpPopover } from '@components/ui/HelpPopover';
import { PasswordField } from '@components/ui/PasswordField';
import { RowActionsMenu } from '@components/ui/RowActionsMenu';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { useAuth } from '@contexts/useAuth';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useHeldValue } from '@hooks/useHeldValue';
import ApiService from '@services/api.service';
import authService from '@services/auth.service';
import { usesOidc } from '@utils/accountMode';
import { API_BASE } from '@utils/constants';
import { getErrorMessage } from '@utils/error';
import type { LoginService } from '@utils/loginService';
import MetricsAccessRow from './MetricsAccessRow';
import UserPaneHeader from './UserPaneHeader';
import UserPaneSection from './UserPaneSection';
import UserSettingRow from './UserSettingRow';

/**
 * The installation-wide sign-in method and its tested connections, beside the accounts it governs.
 * The primary administrator changes the method here, removes connections and can wipe every
 * account; an installation running without sign-in shows the pane so the method can be switched on.
 */
const SignInPane: React.FC = () => {
  const { t } = useTranslation();
  const {
    isMainAdmin,
    authenticationEnabled,
    accountMode,
    signInModeFromEnvironment,
    loginServices,
    refreshAuth
  } = useAuth();
  const { notifyError } = useErrorHandler();
  const [editingAccess, setEditingAccess] = useState(false);
  // Counts the times the wizard was opened. The wizard stays drawn after it closes so its fade
  // plays, and each opening gets a new key so it starts from the current sign-in state.
  const [accessOpenings, setAccessOpenings] = useState(0);
  const [removing, setRemoving] = useState<LoginService | null>(null);
  const heldRemoving = useHeldValue(removing);
  const [removeKey, setRemoveKey] = useState('');
  const [removeBusy, setRemoveBusy] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);
  const [menuOpen, setMenuOpen] = useState(false);
  const [wipeOpened, setWipeOpened] = useState(false);
  const [wiping, setWiping] = useState(false);

  if (!isMainAdmin && authenticationEnabled) {
    return null;
  }

  const closeRemove = () => {
    if (removeBusy) return;
    setRemoving(null);
    setRemoveKey('');
    setRemoveError(null);
  };

  const removeService = async () => {
    if (!removing || !removeKey.trim()) return;
    setRemoveBusy(true);
    setRemoveError(null);
    try {
      await authService.removeLoginService(removing.id, removeKey.trim());
      setRemoving(null);
      setRemoveKey('');
      await refreshAuth();
    } catch (error: unknown) {
      setRemoveError(getErrorMessage(error));
    } finally {
      setRemoveBusy(false);
    }
  };

  const wipeAccounts = async () => {
    try {
      setWiping(true);
      const response = await fetch(
        `${API_BASE}/accounts/wipe`,
        ApiService.getJsonFetchOptions({}, { method: 'POST' })
      );
      await ApiService.handleResponse<{ message: string }>(response);
    } catch (err: unknown) {
      notifyError(t('user.accounts.errors.wipe'), err, {
        logLabel: 'Failed to wipe accounts'
      });
      setWiping(false);
      return;
    }

    // The delete already ended every session, this one included, so the page reloads signed out
    // with no sign-out request for the server to refuse.
    window.location.reload();
  };

  const modeTitle = t(`accessSetup.modes.${accountMode}.title`);
  const headerLines: React.ReactNode[] = signInModeFromEnvironment
    ? [modeTitle, t('accessSetup.environment.setByEnvironment')]
    : [modeTitle];

  // With authentication off the shared session reports isMainAdmin true for every visitor, while
  // the wipe endpoint still refuses a caller with no account row.
  const canWipe = authenticationEnabled && isMainAdmin;

  return (
    <>
      {accessOpenings > 0 && (
        <AccessSetup
          key={accessOpenings}
          opened={editingAccess}
          onClose={() => setEditingAccess(false)}
        />
      )}
      <ConfirmationModal
        opened={removing !== null}
        onClose={closeRemove}
        onConfirm={() => void removeService()}
        loading={removeBusy}
        confirmDisabled={!removeKey.trim()}
        confirmLabel={t('accessSetup.remove')}
        title={t('accessSetup.removeService', { name: heldRemoving?.displayName ?? '' })}
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t(
              usesOidc(accountMode)
                ? 'accessSetup.removeServiceActive'
                : 'accessSetup.removeServiceDescription'
            )}
          </p>
          <PasswordField
            label={t('modals.auth.labels.apiKey')}
            value={removeKey}
            onChange={(event) => setRemoveKey(event.target.value)}
            autoComplete="new-password"
            inputClassName="w-full px-3 py-2.5 themed-input"
            showPasswordLabel={t('accessSetup.showSecret')}
            hidePasswordLabel={t('accessSetup.hideSecret')}
          />
          {removeError && (
            <div role="alert">
              <Alert color="error" icon={null} title={t('accessSetup.removeFailed')}>
                {removeError}
              </Alert>
            </div>
          )}
        </div>
      </ConfirmationModal>
      <ConfirmationModal
        opened={wipeOpened}
        onClose={() => setWipeOpened(false)}
        onConfirm={wipeAccounts}
        loading={wiping}
        title={t('user.accounts.confirm.wipeTitle')}
        confirmLabel={t('user.accounts.actions.wipe')}
        confirmColor="red"
      >
        <p className="text-sm text-themed-secondary">{t('user.accounts.confirm.wipeBody')}</p>
      </ConfirmationModal>
      <UserPaneHeader
        title={t('user.panes.list.signIn')}
        lines={headerLines}
        actions={
          canWipe ? (
            <RowActionsMenu open={menuOpen} onOpenChange={setMenuOpen} width="w-60">
              {(close: () => void) => (
                <ActionMenuItem
                  onClick={() => {
                    close();
                    setWipeOpened(true);
                  }}
                >
                  {t('user.accounts.actions.wipe')}
                </ActionMenuItem>
              )}
            </RowActionsMenu>
          ) : undefined
        }
      />
      <div className="users-pane-body">
        <UserPaneSection title={t('user.panes.sections.howUsersSignIn')}>
          <UserSettingRow label={modeTitle}>
            <Button
              variant="default"
              size="sm"
              className="users-pane-btn pointer-target-44"
              onClick={() => {
                setAccessOpenings((count: number) => count + 1);
                setEditingAccess(true);
              }}
            >
              {t('accessSetup.change')}
            </Button>
          </UserSettingRow>
          {authenticationEnabled && <MetricsAccessRow />}
          {isMainAdmin &&
            loginServices.map((service: LoginService) => {
              const kindTitle = t(`accessSetup.services.${service.kind}.title`);
              const use = t(
                usesOidc(accountMode)
                  ? 'accessSetup.connectionActive'
                  : 'accessSetup.connectionDormant'
              );
              // The server refuses removing a login an environment variable sets.
              const environmentManaged = service.environmentFields.length > 0;
              return (
                <UserSettingRow
                  key={service.id}
                  label={service.displayName}
                  help={
                    <HelpPopover
                      aria-label={t('user.panes.help.serviceAria', { name: service.displayName })}
                    >
                      {kindTitle === service.displayName ? use : `${kindTitle} · ${use}`}
                    </HelpPopover>
                  }
                >
                  {environmentManaged && (
                    <span className="text-sm text-themed-muted">
                      {t('accessSetup.environment.setByEnvironment')}
                    </span>
                  )}
                  <Button
                    variant="default"
                    size="sm"
                    className="users-pane-btn pointer-target-44"
                    disabled={removeBusy || environmentManaged}
                    onClick={() => setRemoving(service)}
                  >
                    {t('accessSetup.remove')}
                  </Button>
                </UserSettingRow>
              );
            })}
        </UserPaneSection>
      </div>
    </>
  );
};

export default SignInPane;
