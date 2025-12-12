import React from 'react';
import { AlertTriangle, Trash2 } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { formatBytes } from '@utils/formatters';
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
          <span>Remove {isGame ? 'Game' : 'Service'} from Cache</span>
        </div>
      }
    >
      <div className="space-y-4">
        <p className="text-themed-secondary">
          Are you sure you want to remove{' '}
          <span className={`font-semibold text-themed-primary ${!isGame ? 'capitalize' : ''}`}>
            {name}
          </span>
          {!isGame && ' service'} from cache?
        </p>

        <Alert color="yellow">
          <div>
            <p className="text-xs font-medium mb-2">This will:</p>
            <ul className="list-disc list-inside text-xs space-y-1 ml-2">
              <li>Delete approximately {filesCount.toLocaleString()} cache files</li>
              <li>Free up approximately {formatBytes(totalSize)}</li>
              {isGame && (
                <li>
                  Remove cache for {depotCount} depot{depotCount !== 1 ? 's' : ''}
                </li>
              )}
              {!isGame && (
                <>
                  <li>Remove ALL log entries for this service from the database</li>
                  <li>Remove ALL download records for this service from the database</li>
                </>
              )}
              <li>Progress will be shown in the notification bar at the top</li>
              <li>This action cannot be undone</li>
            </ul>
          </div>
        </Alert>

        <div className="flex justify-end gap-3 pt-2">
          <Button variant="default" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="filled"
            color="red"
            leftSection={<Trash2 className="w-4 h-4" />}
            onClick={onConfirm}
          >
            Remove from Cache
          </Button>
        </div>
      </div>
    </Modal>
  );
};

export default CacheRemovalModal;

// Re-export for backwards compatibility with existing imports
export { CacheRemovalModal };
