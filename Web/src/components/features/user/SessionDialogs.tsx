import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Trash2 } from 'lucide-react';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Alert } from '@components/ui/Alert';
import ApiService from '@services/api.service';
import { isAccountHolder } from '@services/auth.service';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useHeldValue } from '@hooks/useHeldValue';
import { type Session, deviceTitle, sessionName } from './types';

interface SessionDialogsProps {
  revoking: Session | null;
  onRevokeClose: () => void;
  deleting: Session | null;
  onDeleteClose: () => void;
  onSessionsChange: () => void;
}

// Rendered by the page at all times, so a dialog keeps its subject and its fade when the session it
// names leaves the active list while the request is out.
const SessionDialogs: React.FC<SessionDialogsProps> = ({
  revoking,
  onRevokeClose,
  deleting,
  onDeleteClose,
  onSessionsChange
}) => {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
  const [revokeInFlight, setRevokeInFlight] = useState(false);
  const [deleteInFlight, setDeleteInFlight] = useState(false);
  const shownRevoking = useHeldValue(revoking);
  const shownDeleting = useHeldValue(deleting);

  // A failed request keeps the dialog open with its confirm button, so the admin can try again.
  const confirmRevokeSession = async (): Promise<void> => {
    if (!revoking || revokeInFlight) return;
    try {
      setRevokeInFlight(true);
      await ApiService.revokeSession(revoking.id);
      onSessionsChange();
      onRevokeClose();
    } catch (err: unknown) {
      notifyError(t('activeSessions.errors.revokeSession'), err, {
        logLabel: 'Failed to revoke session'
      });
    } finally {
      setRevokeInFlight(false);
    }
  };

  const confirmDeleteSession = async (): Promise<void> => {
    if (!deleting || deleteInFlight) return;
    try {
      setDeleteInFlight(true);
      await ApiService.deleteSession(deleting.id);
      onSessionsChange();
      onDeleteClose();
    } catch (err: unknown) {
      notifyError(t('activeSessions.errors.deleteSession'), err, {
        logLabel: 'Failed to delete session'
      });
    } finally {
      setDeleteInFlight(false);
    }
  };

  return (
    <>
      <ConfirmationModal
        opened={revoking !== null}
        onClose={onRevokeClose}
        onConfirm={confirmRevokeSession}
        title={t('activeSessions.revokeModal.title')}
        confirmLabel={t('activeSessions.revokeModal.confirm')}
        confirmColor="orange"
        loading={revokeInFlight}
      >
        <p className="text-themed-secondary">
          {t('activeSessions.revokeModal.message', {
            type:
              shownRevoking && isAccountHolder(shownRevoking.sessionType ?? null)
                ? t('activeSessions.sessionTypes.accountSession')
                : t('activeSessions.sessionTypes.guestSession')
          })}
        </p>

        {shownRevoking && (
          <div className="mgmt-panel">
            <p className="text-sm text-themed-primary font-medium">
              {sessionName(shownRevoking, t)}
            </p>
            <p className="text-xs text-themed-muted">{deviceTitle(shownRevoking.userAgent, t)}</p>
            <p className="text-xs text-themed-muted font-mono">
              {t('activeSessions.labels.sessionIdWithValue', { id: shownRevoking.id })}
            </p>
          </div>
        )}

        <Alert color="yellow" icon={null}>
          <p className="text-sm">{t('activeSessions.revokeModal.summary')}</p>
        </Alert>
      </ConfirmationModal>

      {/* A red trash separates the permanent delete from the revoke above, which only ends the
          session and keeps its history. */}
      <ConfirmationModal
        opened={deleting !== null}
        onClose={onDeleteClose}
        onConfirm={confirmDeleteSession}
        title={t('activeSessions.deleteModal.title')}
        icon={<Trash2 className="w-6 h-6 text-themed-error" />}
        confirmLabel={t('activeSessions.deleteModal.confirm')}
        loading={deleteInFlight}
      >
        <p className="text-themed-secondary">
          {t('activeSessions.deleteModal.message', {
            type:
              shownDeleting && isAccountHolder(shownDeleting.sessionType ?? null)
                ? t('activeSessions.sessionTypes.accountSession')
                : t('activeSessions.sessionTypes.guestSession')
          })}
        </p>

        {shownDeleting && (
          <div className="mgmt-panel">
            <p className="text-sm text-themed-primary font-medium">
              {sessionName(shownDeleting, t)}
            </p>
            <p className="text-xs text-themed-muted">{deviceTitle(shownDeleting.userAgent, t)}</p>
            <p className="text-xs text-themed-muted font-mono">
              {t('activeSessions.labels.sessionIdWithValue', { id: shownDeleting.id })}
            </p>
          </div>
        )}

        <Alert color="red" icon={null}>
          {/* An ended session is signed out already, so its summary only says what the delete removes. */}
          <p className="text-sm">
            {shownDeleting && (shownDeleting.isRevoked || shownDeleting.isExpired)
              ? t('activeSessions.deleteModal.endedSummary')
              : t('activeSessions.deleteModal.summary')}
          </p>
        </Alert>
      </ConfirmationModal>
    </>
  );
};

export default SessionDialogs;
