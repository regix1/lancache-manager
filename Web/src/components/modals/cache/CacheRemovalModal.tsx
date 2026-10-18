import React, { useMemo } from 'react';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Alert } from '@components/ui/Alert';
import { formatBytes, formatCount } from '@utils/formatters';
import { useHeldValue } from '@hooks/useHeldValue';
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
  /** Total size of the counted files; null until a count has returned (or when none was run). */
  fileBytes: number | null;
  /** What the count is doing, or why it produced no number. Shown until `fileCount` arrives. */
  statusMessage?: string;
  confirmDisabled?: boolean;
  /** True while the count is still walking, so the confirm button spins rather than only graying out. */
  confirmBusy?: boolean;
}

const CacheRemovalModal: React.FC<CacheRemovalModalProps> = ({
  target,
  onClose,
  onConfirm,
  titleOverride,
  descriptionOverride,
  evictedCount,
  evictedBytes,
  fileCount: liveFileCount,
  fileBytes: liveFileBytes,
  statusMessage: liveStatusMessage,
  confirmDisabled: liveConfirmDisabled,
  confirmBusy: liveConfirmBusy
}) => {
  const { t } = useTranslation();
  // The dialog closes when `target` clears, and the parent resets the shared count figures when
  // another removal dialog opens. The held copy keeps what it showed on screen while it fades.
  const live = useMemo(
    () => ({
      shownTarget: target,
      fileCount: liveFileCount,
      fileBytes: liveFileBytes,
      statusMessage: liveStatusMessage,
      confirmDisabled: liveConfirmDisabled,
      confirmBusy: liveConfirmBusy
    }),
    [target, liveFileCount, liveFileBytes, liveStatusMessage, liveConfirmDisabled, liveConfirmBusy]
  );
  const { shownTarget, fileCount, fileBytes, statusMessage, confirmDisabled, confirmBusy } =
    useHeldValue(target ? live : null) ?? live;

  const isGame = shownTarget?.type === 'game';
  const isEvictedRemoval = titleOverride !== undefined && evictedCount !== undefined;

  const modalTitle =
    titleOverride ??
    (isGame ? t('modals.cacheRemoval.titleGame') : t('modals.cacheRemoval.titleService'));

  let body: React.ReactNode = null;
  if (shownTarget) {
    const name = isGame ? shownTarget.data.game_name : shownTarget.data.service_name;
    const filesCount = shownTarget.data.cache_files_found;
    const totalSize = shownTarget.data.total_size_bytes;
    const depotCount = isGame ? shownTarget.data.depot_ids.length : 0;
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
    } else if (fileCount !== undefined && fileBytes !== null) {
      summary = t(
        isGame
          ? 'modals.cacheRemoval.summaryGameCounted'
          : 'modals.cacheRemoval.summaryServiceCounted',
        { formattedCount: formatCount(fileCount), size: formatBytes(fileBytes) }
      );
    } else if (statusMessage !== undefined) {
      summary = statusMessage;
    } else {
      summary = t(
        isGame ? 'modals.cacheRemoval.summaryGame' : 'modals.cacheRemoval.summaryService',
        {
          formattedCount: formatCount(filesCount),
          size: formatBytes(totalSize)
        }
      );
    }

    body = (
      <>
        <p className="text-themed-secondary">{modalDescription}</p>

        {/* One sentence rather than the six bullets this used to be. The old list spelled out the
            record tables and where progress appears, which is not what the reader is deciding. */}
        <Alert color="yellow" icon={null}>
          <p className="text-sm">
            {summary}
            {!isEvictedRemoval && isGame && depotCount > 0
              ? ` ${t('modals.cacheRemoval.depotScope', { count: depotCount })}`
              : null}
          </p>
        </Alert>
      </>
    );
  }

  return (
    <ConfirmationModal
      opened={target !== null}
      onClose={onClose}
      onConfirm={onConfirm}
      title={modalTitle}
      confirmDisabled={confirmDisabled}
      confirmBusy={confirmBusy}
      confirmLabel={
        titleOverride !== undefined && evictedCount !== undefined
          ? t('modals.cacheRemoval.removeEvictedButton')
          : t('modals.cacheRemoval.removeButton')
      }
    >
      {body}
    </ConfirmationModal>
  );
};

export default CacheRemovalModal;
