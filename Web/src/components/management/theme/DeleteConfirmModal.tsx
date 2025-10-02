import React from 'react';
import { AlertTriangle, Trash2 } from 'lucide-react';
import { Modal } from '../../ui/Modal';
import { Alert } from '../../ui/Alert';
import { Button } from '../../ui/Button';

interface DeleteConfirmModalProps {
  opened: boolean;
  onClose: () => void;
  onConfirm: () => void;
  themeName: string | null;
  loading?: boolean;
}

export const DeleteConfirmModal: React.FC<DeleteConfirmModalProps> = ({
  opened,
  onClose,
  onConfirm,
  themeName,
  loading = false
}) => {
  return (
    <Modal
      opened={opened}
      onClose={() => {
        if (!loading) {
          onClose();
        }
      }}
      title={
        <div className="flex items-center space-x-3">
          <AlertTriangle className="w-6 h-6 text-themed-warning" />
          <span>Delete Theme</span>
        </div>
      }
    >
      <div className="space-y-4">
        <p className="text-themed-secondary">
          Delete theme <strong>{themeName}</strong>? This will permanently remove the theme files from
          the server.
        </p>

        <Alert color="yellow">
          <p className="text-sm">This action cannot be undone.</p>
        </Alert>

        <div className="flex justify-end space-x-3 pt-2">
          <Button variant="default" onClick={onClose} disabled={loading}>
            Cancel
          </Button>
          <Button
            variant="filled"
            color="red"
            leftSection={<Trash2 className="w-4 h-4" />}
            onClick={onConfirm}
            loading={loading}
          >
            Delete Theme
          </Button>
        </div>
      </div>
    </Modal>
  );
};
