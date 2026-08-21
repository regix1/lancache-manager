import React from 'react';
import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';

interface ConfirmationModalProps {
  opened: boolean;
  onClose: () => void;
  onConfirm: () => void;
  title: string;
  children: React.ReactNode;
  confirmLabel?: string;
  confirmColor?:
    | 'destructive'
    | 'blue'
    | 'green'
    | 'red'
    | 'yellow'
    | 'purple'
    | 'gray'
    | 'orange'
    | 'default';
  loading?: boolean;
  confirmDisabled?: boolean;
  /**
   * Replaces the default warning triangle in the title row. Pass a `w-6 h-6` icon when the dialog
   * needs a stronger or gentler signal than "caution" — e.g. a red trash for a permanent delete, or
   * a shield for lifting a ban. The default suits any ordinary destructive confirmation.
   */
  icon?: React.ReactNode;
  /** Widen the dialog when the body carries a list or a scroll region rather than a sentence. */
  size?: 'sm' | 'md' | 'lg' | 'xl' | '2xl' | 'full';
}

export const ConfirmationModal: React.FC<ConfirmationModalProps> = ({
  opened,
  onClose,
  onConfirm,
  title,
  children,
  confirmLabel,
  confirmColor = 'destructive',
  loading = false,
  confirmDisabled = false,
  icon,
  size = 'md'
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
          {icon ?? <AlertTriangle className="w-6 h-6 text-themed-warning" />}
          <span>{title}</span>
        </div>
      }
      size={size}
    >
      <div className="space-y-4">
        {children}

        {/* Buttons stack full-width below the `sm` breakpoint so neither one is squeezed to a few
            characters wide on a phone. Confirm sits on top there (reversed column) to stay closest
            to the thumb, while the desktop row keeps cancel-then-confirm reading order. */}
        <div className="flex flex-col-reverse sm:flex-row justify-end gap-3 pt-2">
          <Button
            variant="default"
            onClick={onClose}
            disabled={loading}
            className="w-full sm:w-auto"
          >
            {t('common.cancel')}
          </Button>
          <Button
            variant="filled"
            color={confirmColor}
            onClick={onConfirm}
            loading={loading}
            stableWidth
            disabled={confirmDisabled}
            aria-busy={loading}
            className="w-full sm:w-auto"
          >
            {confirmLabel || t('common.confirm')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};
