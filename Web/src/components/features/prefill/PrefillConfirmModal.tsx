import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { AlertCircle, Loader2 } from 'lucide-react';
import { CommandType, formatBytes } from './types';

interface EstimatedSizeApp {
  appId: number;
  name: string;
  downloadSize: number;
  isUnsupportedOs?: boolean;
  unavailableReason?: string;
}

interface EstimatedSize {
  bytes: number;
  loading: boolean;
  error?: string;
  apps?: EstimatedSizeApp[];
  message?: string;
}

interface PrefillConfirmModalProps {
  pendingCommand: CommandType | null;
  estimatedSize: EstimatedSize;
  onConfirm: () => void;
  onCancel: () => void;
  getConfirmationMessage: (command: CommandType) => { title: string; message: string };
}

export function PrefillConfirmModal({
  pendingCommand,
  estimatedSize,
  onConfirm,
  onCancel,
  getConfirmationMessage
}: PrefillConfirmModalProps) {
  if (!pendingCommand) return null;

  const { title, message } = getConfirmationMessage(pendingCommand);

  return (
    <Modal
      opened={!!pendingCommand}
      onClose={onCancel}
      title={
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0 bg-[color-mix(in_srgb,var(--theme-warning)_15%,transparent)]">
            <AlertCircle className="h-5 w-5 text-[var(--theme-warning)]" />
          </div>
          <span>{title}</span>
        </div>
      }
      size="md"
    >
      <div className="space-y-4">
        <p className="text-sm text-themed-muted">{message}</p>

        {/* Estimated download size */}
        {pendingCommand === 'prefill' && (
          <div className="p-3 rounded-lg bg-[var(--theme-bg-secondary)]">
            {estimatedSize.loading ? (
              <div className="flex items-center gap-2">
                <Loader2 className="h-4 w-4 animate-spin text-[var(--theme-primary)]" />
                <span className="text-sm text-themed-muted">Calculating download size...</span>
              </div>
            ) : estimatedSize.error ? (
              <span className="text-sm text-themed-muted">{estimatedSize.error}</span>
            ) : (
              <div className="space-y-2">
                <div className="flex items-center justify-between">
                  <span className="text-sm text-themed-muted">Total estimated download:</span>
                  <span className="text-sm font-semibold text-[var(--theme-primary)]">
                    {formatBytes(estimatedSize.bytes)}
                  </span>
                </div>
                {estimatedSize.apps && estimatedSize.apps.length > 0 && (
                  <div className="pt-2 border-t border-[var(--theme-border-primary)]">
                    <div className="text-xs text-themed-muted mb-1">
                      Breakdown ({estimatedSize.apps.length} games):
                    </div>
                    <div className="space-y-1 max-h-32 overflow-y-auto">
                      {estimatedSize.apps.map((app) => (
                        <div
                          key={app.appId}
                          className={`flex items-center justify-between text-xs ${
                            app.isUnsupportedOs ? 'opacity-50' : ''
                          }`}
                        >
                          <span
                            className={`truncate mr-2 max-w-[200px] ${
                              app.isUnsupportedOs
                                ? 'text-themed-muted line-through'
                                : 'text-themed-secondary'
                            }`}
                            title={app.unavailableReason || app.name}
                          >
                            {app.name}
                          </span>
                          <span
                            className={`whitespace-nowrap ${
                              app.isUnsupportedOs ? 'text-amber-500' : 'text-themed-muted'
                            }`}
                            title={app.unavailableReason}
                          >
                            {app.isUnsupportedOs
                              ? app.unavailableReason || 'Unsupported OS'
                              : formatBytes(app.downloadSize)}
                          </span>
                        </div>
                      ))}
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>
        )}

        <div className="flex justify-end gap-3 pt-2">
          <Button variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button
            variant="filled"
            color="blue"
            onClick={onConfirm}
            disabled={pendingCommand === 'prefill' && estimatedSize.loading}
          >
            {pendingCommand === 'prefill' ? 'Start Download' : 'Yes, Continue'}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
