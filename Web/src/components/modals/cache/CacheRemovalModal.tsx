import React from 'react';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Alert } from '@components/ui/Alert';
import { formatBytes, formatCount } from '@utils/formatters';
import { useTranslation } from 'react-i18next';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../types';

type RemovalTarget =
  | { type: 'game'; data: GameCacheInfo }
  | { type: 'service'; data: ServiceCacheInfo };

interface CacheRemovalModalProps {
  target: RemovalTarget | null;
  onClose: () => void;
  onConfirm: () => void;
  titleOverride?: string;
  descriptionOverride?: string;
  evictedCount?: number;
  evictedBytes?: number;
  /**
   * Files the removal will actually delete, counted against the disk when this dialog opened.
   * Replaces the detection snapshot's number, which describes a different set: the snapshot
   * counts what the last scan saw, the removal reaches every URL the service ever logged.
   */
  fileCount?: number;
  /** What the count is doing, or why it produced no number. Shown until `fileCount` arrives. */
  statusMessage?: string;
  confirmDisabled?: boolean;
}

const CacheRemovalModal: React.FC<CacheRemovalModalProps> = ({
  target,
  onClose,
  onConfirm,
  titleOverride,
  descriptionOverride,
  evictedCount,
  evictedBytes,
  fileCount,
  statusMessage,
  confirmDisabled
}) => {
  const { t } = useTranslation();

  if (!target) return null;

  const isGame = target.type === 'game';
  const name = isGame
    ? (target.data as GameCacheInfo).game_name
    : (target.data as ServiceCacheInfo).service_name;
  const filesCount = target.data.cache_files_found;
  const totalSize = target.data.total_size_bytes;
  const depotCount = isGame ? (target.data as GameCacheInfo).depot_ids.length : 0;
  const isEvictedRemoval = titleOverride !== undefined && evictedCount !== undefined;

  const modalTitle =
    titleOverride ??
    (isGame ? t('modals.cacheRemoval.titleGame') : t('modals.cacheRemoval.titleService'));
  const modalDescription =
    descriptionOverride ??
    (isGame
      ? t('modals.cacheRemoval.confirmGame', { name })
      : t('modals.cacheRemoval.confirmService', { name }));

  // A counted number describes exactly what the removal will delete, so it replaces the
  // detection snapshot rather than sitting beside it. Until the count returns there is no
  // honest number to show, only what the count is doing.
  let summary: string;
  if (isEvictedRemoval) {
    summary = t('modals.cacheRemoval.summaryEvicted', {
      count: evictedCount,
      size: formatBytes(evictedBytes ?? 0)
    });
  } else if (fileCount !== undefined) {
    summary = t(
      isGame
        ? 'modals.cacheRemoval.summaryGameCounted'
        : 'modals.cacheRemoval.summaryServiceCounted',
      { formattedCount: formatCount(fileCount) }
    );
  } else if (statusMessage !== undefined) {
    summary = statusMessage;
  } else {
    summary = t(isGame ? 'modals.cacheRemoval.summaryGame' : 'modals.cacheRemoval.summaryService', {
      formattedCount: formatCount(filesCount),
      size: formatBytes(totalSize)
    });
  }

  return (
    <ConfirmationModal
      opened={target !== null}
      onClose={onClose}
      onConfirm={onConfirm}
      title={modalTitle}
      confirmDisabled={confirmDisabled}
      confirmLabel={
        titleOverride !== undefined && evictedCount !== undefined
          ? t('modals.cacheRemoval.removeEvictedButton')
          : t('modals.cacheRemoval.removeButton')
      }
    >
      <p className="text-themed-secondary">{modalDescription}</p>

      {/* One sentence rather than the six bullets this used to be. The old list spelled out the
          record tables and where progress appears, which is not what the reader is deciding. */}
      <Alert color="yellow">
        <p className="text-xs">
          {summary}
          {!isEvictedRemoval && isGame && depotCount > 0
            ? ` ${t('modals.cacheRemoval.depotScope', { count: depotCount })}`
            : null}
        </p>
      </Alert>
    </ConfirmationModal>
  );
};

export default CacheRemovalModal;
