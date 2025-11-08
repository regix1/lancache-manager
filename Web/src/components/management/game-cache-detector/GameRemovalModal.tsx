import React from 'react';
import { AlertTriangle, Trash2 } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import type { GameCacheInfo } from '../../../types';

interface GameRemovalModalProps {
  game: GameCacheInfo | null;
  onClose: () => void;
  onConfirm: () => void;
}

const GameRemovalModal: React.FC<GameRemovalModalProps> = ({ game, onClose, onConfirm }) => {
  const formatBytes = (bytes: number): string => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${parseFloat((bytes / Math.pow(k, i)).toFixed(2))} ${sizes[i]}`;
  };

  return (
    <Modal
      opened={game !== null}
      onClose={onClose}
      title={
        <div className="flex items-center space-x-3">
          <AlertTriangle className="w-6 h-6 text-themed-warning" />
          <span>Remove Game from Cache</span>
        </div>
      }
    >
      {game && (
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to remove{' '}
            <span className="font-semibold text-themed-primary">{game.game_name}</span> from cache?
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-xs font-medium mb-2">This will:</p>
              <ul className="list-disc list-inside text-xs space-y-1 ml-2">
                <li>Delete approximately {game.cache_files_found.toLocaleString()} cache files</li>
                <li>Free up approximately {formatBytes(game.total_size_bytes)}</li>
                <li>
                  Remove cache for {game.depot_ids.length} depot
                  {game.depot_ids.length !== 1 ? 's' : ''}
                </li>
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
      )}
    </Modal>
  );
};

export default GameRemovalModal;
