import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, RotateCcw, Eraser } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { showToast } from './types';

interface BulkActionsProps {
  onSessionsChange: () => void;
}

const BulkActions: React.FC<BulkActionsProps> = ({ onSessionsChange }) => {
  const { t } = useTranslation();
  const [bulkActionInProgress, setBulkActionInProgress] = useState<string | null>(null);
  const [showBulkResetConfirm, setShowBulkResetConfirm] = useState(false);
  const [showClearGuestsConfirm, setShowClearGuestsConfirm] = useState(false);

  const handleBulkResetToDefaults = async () => {
    try {
      setBulkActionInProgress('reset');
      const response = await fetch('/api/sessions/bulk/reset-to-defaults', ApiService.getFetchOptions({
        method: 'POST'
      }));

      if (response.ok) {
        const data = await response.json();
        showToast('success', t('user.bulkActions.resetSuccess', { count: data.affectedCount }));
        setShowBulkResetConfirm(false);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('user.bulkActions.errors.resetFailed'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.bulkActions.errors.resetFailed'));
    } finally {
      setBulkActionInProgress(null);
    }
  };

  const handleClearAllGuests = async () => {
    try {
      setBulkActionInProgress('clear');
      const response = await fetch('/api/sessions/bulk/clear-guests', ApiService.getFetchOptions({
        method: 'DELETE'
      }));

      if (response.ok) {
        const data = await response.json();
        showToast('success', t('user.bulkActions.clearSuccess', { count: data.clearedCount }));
        onSessionsChange();
        setShowClearGuestsConfirm(false);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('user.bulkActions.errors.clearFailed'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.bulkActions.errors.clearFailed'));
    } finally {
      setBulkActionInProgress(null);
    }
  };

  return (
    <>
      <div className="bulk-action-card">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
          <div>
            <h3 className="text-base font-semibold flex items-center gap-2 text-themed-primary">
              <AlertTriangle className="w-4 h-4 text-themed-warning" />
              {t('user.bulkActions.title')}
            </h3>
            <p className="text-sm mt-1 text-themed-muted">
              {t('user.bulkActions.subtitle')}
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button
              variant="outline"
              size="sm"
              onClick={() => setShowBulkResetConfirm(true)}
              disabled={!!bulkActionInProgress}
              loading={bulkActionInProgress === 'reset'}
              leftSection={<RotateCcw className="w-4 h-4" />}
            >
              {t('user.bulkActions.buttons.reset')}
            </Button>
            <Button
              variant="outline"
              color="red"
              size="sm"
              onClick={() => setShowClearGuestsConfirm(true)}
              disabled={!!bulkActionInProgress}
              loading={bulkActionInProgress === 'clear'}
              leftSection={<Eraser className="w-4 h-4" />}
            >
              {t('user.bulkActions.buttons.clear')}
            </Button>
          </div>
        </div>
      </div>

      {/* Bulk Reset Confirmation Modal */}
      <Modal
        opened={showBulkResetConfirm}
        onClose={() => {
          if (!bulkActionInProgress) {
            setShowBulkResetConfirm(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <RotateCcw className="w-6 h-6 text-themed-warning" />
            <span>{t('user.bulkActions.resetModal.title')}</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('user.bulkActions.resetModal.message')}
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">{t('user.bulkActions.resetModal.noteTitle')}</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('user.bulkActions.resetModal.points.theme')}</li>
                <li>{t('user.bulkActions.resetModal.points.refreshRate')}</li>
                <li>{t('user.bulkActions.resetModal.points.preferences')}</li>
                <li>{t('user.bulkActions.resetModal.points.active')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowBulkResetConfirm(false)}
              disabled={!!bulkActionInProgress}
            >
              {t('actions.cancel')}
            </Button>
            <Button
              variant="filled"
              color="orange"
              onClick={handleBulkResetToDefaults}
              loading={bulkActionInProgress === 'reset'}
            >
              {t('user.bulkActions.resetModal.confirm')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Clear All Guests Confirmation Modal */}
      <Modal
        opened={showClearGuestsConfirm}
        onClose={() => {
          if (!bulkActionInProgress) {
            setShowClearGuestsConfirm(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Eraser className="w-6 h-6 text-themed-error" />
            <span>{t('user.bulkActions.clearModal.title')}</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('user.bulkActions.clearModal.message')}
          </p>

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">{t('user.bulkActions.clearModal.noteTitle')}</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('user.bulkActions.clearModal.points.deleted')}</li>
                <li>{t('user.bulkActions.clearModal.points.logout')}</li>
                <li>{t('user.bulkActions.clearModal.points.data')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowClearGuestsConfirm(false)}
              disabled={!!bulkActionInProgress}
            >
              {t('actions.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={handleClearAllGuests}
              loading={bulkActionInProgress === 'clear'}
            >
              {t('user.bulkActions.clearModal.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default BulkActions;
