import React, { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { Alert } from '@components/ui/Alert';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { useHeldValue } from '@hooks/useHeldValue';
import { formatBytes, formatCount } from '@utils/formatters';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import type { UnmappedService } from '../../../types';

interface UnmappedRemovalModalProps {
  opened: boolean;
  onClose: () => void;
  onConfirm: () => void;
  onRetry: () => void;
  /** Files the removal would delete; null until the count returns. */
  files: number | null;
  bytes: number | null;
  services: UnmappedService[];
  /** The count failed before it produced a number. */
  failed: boolean;
  /** Why the count failed, when the server said; shown under the failure line. */
  failureReason: string | null;
  /** What the running count is doing. Shown beside the spinner until the count returns. */
  statusMessage: string;
  /** True from the confirm click until the server answers; only Confirm shows it. */
  confirming: boolean;
  /** Why the server refused the removal; stays until a new count or confirm replaces it. */
  error: string | null;
}

const UnmappedRemovalModal: React.FC<UnmappedRemovalModalProps> = ({
  opened,
  onClose,
  onConfirm,
  onRetry,
  files: liveFiles,
  bytes: liveBytes,
  services: liveServices,
  failed: liveFailed,
  failureReason: liveFailureReason,
  statusMessage: liveStatusMessage,
  confirming: liveConfirming,
  error: liveError
}) => {
  const { t } = useTranslation();

  // The parent shares one set of count figures with the game and service removal dialogs and
  // resets them when either opens, and clears the busy flag as the dialog closes. A dialog that is
  // fading out keeps what it showed as it closed.
  const liveFigures = useMemo(
    () => ({
      files: liveFiles,
      bytes: liveBytes,
      services: liveServices,
      failed: liveFailed,
      failureReason: liveFailureReason,
      statusMessage: liveStatusMessage,
      confirming: liveConfirming,
      error: liveError
    }),
    [
      liveFiles,
      liveBytes,
      liveServices,
      liveFailed,
      liveFailureReason,
      liveStatusMessage,
      liveConfirming,
      liveError
    ]
  );
  const { files, bytes, services, failed, failureReason, statusMessage, confirming, error } =
    useHeldValue(opened ? liveFigures : null) ?? liveFigures;

  let countBody: React.ReactNode;
  if (failed) {
    countBody = (
      <Alert
        color="red"
        action={
          <Button type="button" size="sm" variant="default" onClick={onRetry}>
            {t('common.retry')}
          </Button>
        }
      >
        <p className="text-sm">{t('modals.unmappedRemoval.countFailed')}</p>
        {failureReason && <p className="text-sm">{failureReason}</p>}
      </Alert>
    );
  } else if (files === null || bytes === null) {
    countBody = (
      <div className="flex items-center gap-2 text-sm text-themed-secondary" role="status">
        <LoadingSpinner inline size="sm" className="text-themed-accent flex-shrink-0" />
        <span>{statusMessage}</span>
      </div>
    );
  } else if (files === 0) {
    countBody = (
      <p className="text-sm text-themed-secondary">{t('modals.unmappedRemoval.nothingToRemove')}</p>
    );
  } else {
    countBody = (
      <>
        <p className="font-medium text-themed-primary tabular-nums">
          {t('modals.unmappedRemoval.summary', {
            count: files,
            formattedCount: formatCount(files),
            size: formatBytes(bytes)
          })}
        </p>
        <CustomScrollbar
          maxHeight="none"
          paddingMode="none"
          radius="none"
          className="unmapped-removal-count__list"
        >
          <ul className="divided-list">
            {services.map((service) => (
              <li
                key={service.service}
                className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm"
              >
                <span className="text-themed-secondary truncate">
                  {getServiceDisplayName(service.service)}
                </span>
                <span className="flex flex-wrap items-center justify-end gap-2">
                  <Badge variant="neutral" className="badge-count badge-count-warning tabular-nums">
                    {t('management.gameDetection.unmappedFileCount', {
                      count: service.file_count,
                      formattedCount: formatCount(service.file_count)
                    })}
                  </Badge>
                  <Badge variant="neutral" className="badge-count tabular-nums">
                    {formatBytes(service.total_bytes)}
                  </Badge>
                </span>
              </li>
            ))}
          </ul>
        </CustomScrollbar>
      </>
    );
  }

  return (
    <ConfirmationModal
      opened={opened}
      onClose={onClose}
      onConfirm={onConfirm}
      title={t('modals.unmappedRemoval.title')}
      confirmLabel={t('modals.unmappedRemoval.confirm')}
      confirmColor="red"
      confirmDisabled={!files}
      loading={confirming}
    >
      <p className="text-themed-secondary">{t('modals.unmappedRemoval.description')}</p>
      <div className="unmapped-removal-count">{countBody}</div>
      {error !== null && (
        <Alert color="red">
          <p className="text-sm">{error}</p>
        </Alert>
      )}
    </ConfirmationModal>
  );
};

export default UnmappedRemovalModal;
