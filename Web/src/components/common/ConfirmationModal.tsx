import React from 'react';
import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';

interface ConfirmationModalProps {
  opened: boolean;
  onClose: () => void;
  onConfirm: () => void;
  title: string;
  children: React.ReactNode;
  confirmLabel?: string;
  /** Label for the Cancel button, for a dialog whose way out means something other than "Cancel". */
  cancelLabel?: string;
  confirmColor?:
    | 'destructive'
    | 'run'
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
   * Spins the confirm button while the dialog waits on work it did not start itself. Unlike
   * `loading` it leaves Cancel enabled and the close handler intact, which is what a dialog needs
   * when closing it is how the user cancels that work.
   */
  confirmBusy?: boolean;
  /**
   * Says what the dialog is waiting on while `loading` or `confirmBusy` is set. When given, the
   * spinner moves out of the confirm button to this line at the start of the actions row, and the
   * button only goes disabled with its label kept. Unset keeps the spinner inside the button.
   */
  busyLabel?: string;
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
  cancelLabel,
  confirmColor = 'destructive',
  loading = false,
  confirmDisabled = false,
  confirmBusy = false,
  busyLabel,
  icon,
  size = 'md'
}) => {
  const { t } = useTranslation();
  const busy = loading || confirmBusy;

  return (
    <Modal
      opened={opened}
      onClose={() => {
        if (!loading) {
          onClose();
        }
      }}
      title={
        <div className="confirmation-modal__title">
          <span className="confirmation-modal__title-icon">
            {icon ?? <AlertTriangle className="w-6 h-6 text-themed-warning" />}
          </span>
          <span className="confirmation-modal__title-text">{title}</span>
        </div>
      }
      size={size}
    >
      <div className="confirmation-modal__content">
        {children}

        <div className="confirmation-modal__actions">
          {busyLabel && busy && (
            <span className="confirmation-modal__status" role="status">
              <LoadingSpinner inline size="xs" />
              {busyLabel}
            </span>
          )}
          <Button
            variant="default"
            onClick={onClose}
            disabled={loading}
            className="min-h-[44px] sm:min-h-10"
          >
            {cancelLabel ?? t('common.cancel')}
          </Button>
          <Button
            variant="filled"
            color={confirmColor}
            onClick={onConfirm}
            loading={!busyLabel && busy}
            stableWidth
            disabled={busyLabel ? confirmDisabled || busy : confirmDisabled}
            aria-busy={busy}
            className="min-h-[44px] sm:min-h-10"
          >
            {confirmLabel || t('common.confirm')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};
