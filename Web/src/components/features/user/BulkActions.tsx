import React, { useState } from 'react';
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
  const [bulkActionInProgress, setBulkActionInProgress] = useState<string | null>(null);
  const [showBulkResetConfirm, setShowBulkResetConfirm] = useState(false);
  const [showClearGuestsConfirm, setShowClearGuestsConfirm] = useState(false);

  const handleBulkResetToDefaults = async () => {
    try {
      setBulkActionInProgress('reset');
      const response = await fetch('/api/sessions/bulk/reset-to-defaults', {
        method: 'POST',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        const data = await response.json();
        showToast('success', `Reset ${data.affectedCount} guest sessions to defaults`);
        setShowBulkResetConfirm(false);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to reset guest sessions');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to reset guest sessions');
    } finally {
      setBulkActionInProgress(null);
    }
  };

  const handleClearAllGuests = async () => {
    try {
      setBulkActionInProgress('clear');
      const response = await fetch('/api/sessions/bulk/clear-guests', {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        const data = await response.json();
        showToast('success', `Cleared ${data.clearedCount} guest sessions`);
        onSessionsChange();
        setShowClearGuestsConfirm(false);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to clear guest sessions');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to clear guest sessions');
    } finally {
      setBulkActionInProgress(null);
    }
  };

  return (
    <>
      <div className="bulk-action-card">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
          <div>
            <h3
              className="text-base font-semibold flex items-center gap-2"
              style={{ color: 'var(--theme-text-primary)' }}
            >
              <AlertTriangle className="w-4 h-4" style={{ color: 'var(--theme-warning)' }} />
              Bulk Actions
            </h3>
            <p className="text-sm mt-1" style={{ color: 'var(--theme-text-muted)' }}>
              Apply actions to all guest sessions at once
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
              Reset to Defaults
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
              Clear All Guests
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
            <span>Reset All Guests to Defaults</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to reset all guest session preferences to the default values?
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">This action will:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>Reset theme to default guest theme</li>
                <li>Reset refresh rate to default guest refresh rate</li>
                <li>Reset all display and timezone preferences</li>
                <li>Guest sessions will remain active</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowBulkResetConfirm(false)}
              disabled={!!bulkActionInProgress}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="orange"
              onClick={handleBulkResetToDefaults}
              loading={bulkActionInProgress === 'reset'}
            >
              Reset All Guests
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
            <span>Clear All Guest Sessions</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to remove all guest sessions? This action cannot be undone.
          </p>

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">Warning:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>All guest sessions will be permanently deleted</li>
                <li>All connected guests will be logged out immediately</li>
                <li>Guest preferences and session data will be lost</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowClearGuestsConfirm(false)}
              disabled={!!bulkActionInProgress}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={handleClearAllGuests}
              loading={bulkActionInProgress === 'clear'}
            >
              Clear All Guests
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default BulkActions;
