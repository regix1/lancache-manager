import React from 'react';
import { AlertTriangle, Trash2 } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { formatBytes } from '@utils/formatters';
import { useTranslation } from 'react-i18next';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../types';

type RemovalTarget =
  | { type: 'game'; data: GameCacheInfo }
  | { type: 'service'; data: ServiceCacheInfo };

interface CacheRemovalModalProps {
  target: RemovalTarget | null;
  onClose: () => void;
  onConfirm: () => void;
}

const CacheRemovalModal: React.FC<CacheRemovalModalProps> = ({ target, onClose, onConfirm }) => {
  const { t } = useTranslation();
  
  if (!target) return null;

  const isGame = target.type === 'game';
  const name = isGame
    ? (target.data as GameCacheInfo).game_name
    : (target.data as ServiceCacheInfo).service_name;
  const filesCount = target.data.cache_files_found;
  const totalSize = target.data.total_size_bytes;
  const depotCount = isGame ? (target.data as GameCacheInfo).depot_ids.length : 0;

  return (
    <Modal
      opened={target !== null}
      onClose={onClose}
      title={
        <div className="flex items-center space-x-3">
          <AlertTriangle className="w-6 h-6 text-themed-warning" />
          <span>{isGame ? t('modals.cacheRemoval.titleGame') : t('modals.cacheRemoval.titleService')}</span>
        </div>
      }
    >
      <div className="space-y-4">
        <p className="text-themed-secondary">
          {isGame 
            ? t('modals.cacheRemoval.confirmGame', { name })
            : t('modals.cacheRemoval.confirmService', { name })}
        </p>

        <Alert color="yellow">
          <div>
            <p className="text-xs font-medium mb-2">{t('modals.cacheRemoval.thisWill')}</p>
            <ul className="list-disc list-inside text-xs space-y-1 ml-2">
              <li>{t('modals.cacheRemoval.actions.deleteFiles', {
                count: filesCount,
                formattedCount: filesCount.toLocaleString()
              })}</li>
              <li>{t('modals.cacheRemoval.actions.freeSpace', { size: formatBytes(totalSize) })}</li>
              {isGame && (
                <li>
                  {t('modals.cacheRemoval.actions.removeDepots', { count: depotCount })}
                </li>
              )}
              {!isGame && (
                <>
                  <li>{t('modals.cacheRemoval.actions.removeLogEntries')}</li>
                  <li>{t('modals.cacheRemoval.actions.removeDownloadRecords')}</li>
                </>
              )}
              <li>{t('modals.cacheRemoval.actions.showProgress')}</li>
              <li>{t('modals.cacheRemoval.actions.cannotUndo')}</li>
            </ul>
          </div>
        </Alert>

        <div className="flex justify-end gap-3 pt-2">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            variant="filled"
            color="red"
            leftSection={<Trash2 className="w-4 h-4" />}
            onClick={onConfirm}
          >
            {t('modals.cacheRemoval.removeButton')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};

export default CacheRemovalModal;

// Re-export for backwards compatibility with existing imports
export { CacheRemovalModal };
