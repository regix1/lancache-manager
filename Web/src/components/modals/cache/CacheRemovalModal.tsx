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
}

const CacheRemovalModal: React.FC<CacheRemovalModalProps> = ({
  target,
  onClose,
  onConfirm,
  titleOverride,
  descriptionOverride,
  evictedCount,
  evictedBytes
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

  const modalTitle =
    titleOverride ??
    (isGame ? t('modals.cacheRemoval.titleGame') : t('modals.cacheRemoval.titleService'));
  const modalDescription =
    descriptionOverride ??
    (isGame
      ? t('modals.cacheRemoval.confirmGame', { name })
      : t('modals.cacheRemoval.confirmService', { name }));

  return (
    <ConfirmationModal
      opened={target !== null}
      onClose={onClose}
      onConfirm={onConfirm}
      title={modalTitle}
      confirmLabel={
        titleOverride !== undefined && evictedCount !== undefined
          ? t('modals.cacheRemoval.removeEvictedButton')
          : t('modals.cacheRemoval.removeButton')
      }
    >
      <p className="text-themed-secondary">{modalDescription}</p>

      <Alert color="yellow">
        <div>
          <p className="text-xs font-medium mb-2">{t('modals.cacheRemoval.thisWill')}</p>
          <ul className="list-disc list-inside text-xs space-y-1 ml-2">
            {titleOverride !== undefined && evictedCount !== undefined ? (
              <>
                <li>
                  {t('modals.cacheRemoval.actions.removeEvictedLogEntries', {
                    count: evictedCount
                  })}
                </li>
                <li>
                  {t('modals.cacheRemoval.actions.freeSpace', {
                    size: formatBytes(evictedBytes ?? 0)
                  })}
                </li>
              </>
            ) : (
              <>
                <li>
                  {t('modals.cacheRemoval.actions.deleteFiles', {
                    count: filesCount,
                    formattedCount: formatCount(filesCount)
                  })}
                </li>
                <li>
                  {t('modals.cacheRemoval.actions.freeSpace', { size: formatBytes(totalSize) })}
                </li>
                {isGame && depotCount > 0 && (
                  <li>{t('modals.cacheRemoval.actions.removeDepots', { count: depotCount })}</li>
                )}
                {!isGame && (
                  <>
                    <li>{t('modals.cacheRemoval.actions.removeLogEntries')}</li>
                    <li>{t('modals.cacheRemoval.actions.removeDownloadRecords')}</li>
                  </>
                )}
              </>
            )}
            <li>{t('modals.cacheRemoval.actions.showProgress')}</li>
            <li>{t('modals.cacheRemoval.actions.cannotUndo')}</li>
          </ul>
        </div>
      </Alert>
    </ConfirmationModal>
  );
};

export default CacheRemovalModal;
