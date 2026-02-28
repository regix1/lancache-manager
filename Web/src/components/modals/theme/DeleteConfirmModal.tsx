import React from 'react';
import { AlertTriangle, Trash2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
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
  const { t } = useTranslation();

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
          <span>{t('modals.theme.delete.title')}</span>
        </div>
      }
    >
      <div className="space-y-4">
        <p
          className="text-themed-secondary"
          dangerouslySetInnerHTML={{
            __html: t('modals.theme.delete.message', { name: themeName })
          }}
        />

        <Alert color="yellow">
          <p className="text-sm">{t('modals.theme.delete.warning')}</p>
        </Alert>

        <div className="flex justify-end space-x-3 pt-2">
          <Button variant="default" onClick={onClose} disabled={loading}>
            {t('common.cancel')}
          </Button>
          <Button
            variant="filled"
            color="red"
            leftSection={<Trash2 className="w-4 h-4" />}
            onClick={onConfirm}
            loading={loading}
          >
            {t('modals.theme.delete.confirmButton')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};
