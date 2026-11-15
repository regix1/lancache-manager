import React, { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import FormField from '@components/ui/FormField';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useHeldValue } from '@hooks/useHeldValue';
import { getErrorMessage } from '@utils/error';
import type { AccountConfirmation, AccountEditor } from './types';
import type { UserAccountsState } from './useUserAccounts';

interface AccountDialogsProps {
  /** The open create or edit form, or null. */
  editor: AccountEditor | null;
  onEditorChange: (next: AccountEditor) => void;
  onEditorClose: () => void;
  /** The account waiting for delete confirmation, or null. */
  confirmation: AccountConfirmation | null;
  onConfirmationClose: () => void;
  saveAccount: UserAccountsState['saveAccount'];
  deleteAccount: UserAccountsState['deleteAccount'];
}

/** The create, edit and delete dialogs for accounts. Always mounted so each keeps its fade. */
const AccountDialogs: React.FC<AccountDialogsProps> = ({
  editor,
  onEditorChange,
  onEditorClose,
  confirmation,
  onConfirmationClose,
  saveAccount,
  deleteAccount
}) => {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
  // Both dialogs keep drawing their account while they fade out after it is cleared.
  const shownConfirmation = useHeldValue(confirmation);
  const shownEditor = useHeldValue(editor);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [busyAccountId, setBusyAccountId] = useState<string | null>(null);
  // True while the delete dialog's request is out, so a repeat confirm sends nothing.
  const deleteInProgressRef = useRef(false);

  // A dialog that opens again starts without the refusal its last opening showed. Cleared on
  // open rather than on close, so a closing dialog keeps its text while it fades.
  const [editorOpen, setEditorOpen] = useState(false);
  if ((editor !== null) !== editorOpen) {
    setEditorOpen(editor !== null);
    if (editor !== null) setFormError(null);
  }
  const [confirmationOpen, setConfirmationOpen] = useState(false);
  if ((confirmation !== null) !== confirmationOpen) {
    setConfirmationOpen(confirmation !== null);
    if (confirmation !== null) setDeleteError(null);
  }

  const submitEditor = async (open: AccountEditor) => {
    try {
      setSaving(true);
      setFormError(null);
      await saveAccount(open);
      onEditorClose();
    } catch (err: unknown) {
      // Shown in the form rather than as a notification: the refusal names the field the user is
      // looking at ("that username is already taken"), and the form stays open to be corrected.
      setFormError(getErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  const runConfirmation = async (pending: AccountConfirmation) => {
    if (deleteInProgressRef.current) {
      return;
    }
    deleteInProgressRef.current = true;
    const { account } = pending;
    try {
      setBusyAccountId(account.id);
      await deleteAccount(account);
      onConfirmationClose();
    } catch (err: unknown) {
      // The open dialog shows the reason, so a popup would repeat it.
      setDeleteError(getErrorMessage(err));
      notifyError(t('user.accounts.errors.delete'), err, {
        silent: true,
        logLabel: 'Failed to delete an account'
      });
    } finally {
      setBusyAccountId(null);
      deleteInProgressRef.current = false;
    }
  };

  return (
    <>
      <Modal
        opened={editor !== null}
        onClose={() => {
          if (!saving) {
            onEditorClose();
          }
        }}
        title={
          shownEditor?.account
            ? t('user.accounts.form.editTitle', { username: shownEditor.account.username })
            : t('user.accounts.form.createTitle')
        }
      >
        {shownEditor && (
          <div className="confirmation-modal__content">
            <div>
              <FormField label={t('user.accounts.form.username')}>
                {(field) => (
                  <input
                    {...field}
                    type="text"
                    autoComplete="username"
                    className="w-full px-3 text-sm themed-input control-h-md"
                    value={shownEditor.username}
                    onChange={(event: React.ChangeEvent<HTMLInputElement>) =>
                      onEditorChange({ ...shownEditor, username: event.target.value })
                    }
                  />
                )}
              </FormField>
            </div>

            <div>
              <FormField
                label={
                  shownEditor.account
                    ? t('user.accounts.form.newPassword')
                    : t('user.accounts.form.password')
                }
                hint={shownEditor.account ? t('user.accounts.form.passwordHint') : undefined}
              >
                {(field) => (
                  <input
                    {...field}
                    type="password"
                    autoComplete="new-password"
                    className="w-full px-3 text-sm themed-input control-h-md"
                    value={shownEditor.password}
                    onChange={(event: React.ChangeEvent<HTMLInputElement>) =>
                      onEditorChange({ ...shownEditor, password: event.target.value })
                    }
                  />
                )}
              </FormField>
            </div>

            {formError && <p className="text-sm text-themed-error">{formError}</p>}

            <div className="confirmation-modal__actions">
              <Button variant="default" disabled={saving} onClick={onEditorClose}>
                {t('common.cancel')}
              </Button>
              <Button
                variant="filled"
                color="primary"
                loading={saving}
                disabled={
                  shownEditor.username.trim().length === 0 ||
                  (shownEditor.account === null && shownEditor.password.length === 0)
                }
                onClick={() => submitEditor(shownEditor)}
              >
                {shownEditor.account ? t('common.save') : t('user.accounts.actions.create')}
              </Button>
            </div>
          </div>
        )}
      </Modal>

      <ConfirmationModal
        opened={confirmation !== null}
        onClose={onConfirmationClose}
        onConfirm={() => shownConfirmation && runConfirmation(shownConfirmation)}
        loading={busyAccountId === shownConfirmation?.account.id}
        title={t('user.accounts.confirm.deleteTitle')}
        confirmLabel={t('common.delete')}
        confirmColor="red"
      >
        {shownConfirmation && (
          <div className="space-y-4">
            <p className="text-sm text-themed-secondary">
              {t('user.accounts.confirm.deleteBody', {
                username: shownConfirmation.account.username
              })}
            </p>
            {deleteError && (
              <div role="alert">
                <Alert color="error" icon={null} title={t('user.accounts.errors.delete')}>
                  {deleteError}
                </Alert>
              </div>
            )}
          </div>
        )}
      </ConfirmationModal>
    </>
  );
};

export default AccountDialogs;
