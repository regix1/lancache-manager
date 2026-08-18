import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { API_BASE } from '@utils/constants';

/**
 * Deletes every account, including the signed-in main admin, then ends the session so the
 * first-administrator screen is what comes back. Hidden for anyone who is not the main admin;
 * the server refuses those callers as well.
 */
const WipeAccountsButton: React.FC = () => {
  const { t } = useTranslation();
  const { authenticationEnabled, isMainAdmin, logout } = useAuth();
  const { notifyError } = useErrorHandler();
  const [opened, setOpened] = useState(false);
  const [wiping, setWiping] = useState(false);

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

    await logout();
    window.location.reload();
  };

  // With authentication off the shared session reports isMainAdmin true for every visitor, while
  // the wipe endpoint still refuses a caller with no account row.
  if (!authenticationEnabled || !isMainAdmin) {
    return null;
  }

  return (
    <>
      <Button
        variant="filled"
        color="red"
        size="sm"
        disabled={wiping}
        onClick={() => setOpened(true)}
      >
        {t('user.accounts.actions.wipe')}
      </Button>
      <ConfirmationModal
        opened={opened}
        onClose={() => setOpened(false)}
        onConfirm={wipeAccounts}
        loading={wiping}
        title={t('user.accounts.confirm.wipeTitle')}
        confirmLabel={t('user.accounts.actions.wipe')}
        confirmColor="red"
      >
        <p className="text-sm text-themed-secondary">{t('user.accounts.confirm.wipeBody')}</p>
      </ConfirmationModal>
    </>
  );
};

export default WipeAccountsButton;
