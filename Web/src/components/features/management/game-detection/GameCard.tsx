import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  HardDrive,
  Loader2,
  Database,
  ChevronDown,
  ChevronUp,
  FolderOpen
} from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';
import { formatBytes } from '@utils/formatters';
import type { GameCacheInfo } from '../../../../types';

interface GameCardProps {
  game: GameCacheInfo;
  isExpanded: boolean;
  isExpanding: boolean;
  isRemoving: boolean;
  isAnyRemovalRunning: boolean;
  isAuthenticated: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onToggleDetails: (gameId: number) => void;
  onRemove: (game: GameCacheInfo) => void;
}

const MAX_INITIAL_PATHS = 50;
const MAX_INITIAL_URLS = 20;

const GameCard: React.FC<GameCardProps> = ({
  game,
  isExpanded,
  isExpanding,
  isRemoving,
  isAnyRemovalRunning,
  isAuthenticated,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onToggleDetails,
  onRemove
}) => {
  const { t } = useTranslation();
  const [showAllPaths, setShowAllPaths] = useState(false);
  const [showAllUrls, setShowAllUrls] = useState(false);

  return (
    <div className="rounded-lg border bg-themed-tertiary border-themed-secondary">
      <div className="flex items-center gap-2 p-3">
        <Button
          onClick={() => onToggleDetails(game.game_app_id)}
          variant="subtle"
          size="sm"
          className="flex-shrink-0"
          disabled={isExpanding}
        >
          {isExpanding ? (
            <Loader2 className="w-4 h-4 animate-spin" />
          ) : isExpanded ? (
            <ChevronUp className="w-4 h-4" />
          ) : (
            <ChevronDown className="w-4 h-4" />
          )}
        </Button>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1 flex-wrap">
            <h4 className="text-themed-primary font-semibold break-words">{game.game_name}</h4>
            <span className="text-xs text-themed-muted bg-themed-elevated px-2 py-0.5 rounded flex-shrink-0">
              AppID: {game.game_app_id}
            </span>
          </div>
          <div className="flex items-center gap-3 text-xs text-themed-muted flex-wrap">
            <span className="flex items-center gap-1">
              <FolderOpen className="w-3 h-3" />
              <strong className="text-themed-primary">
                {game.cache_files_found.toLocaleString()}
              </strong>{' '}
              {t('management.gameDetection.files')}
            </span>
            <span className="flex items-center gap-1">
              <HardDrive className="w-3 h-3" />
              <strong className="text-themed-primary">{formatBytes(game.total_size_bytes)}</strong>
            </span>
            <span className="flex items-center gap-1">
              <Database className="w-3 h-3" />
              <strong className="text-themed-primary">{game.depot_ids.length}</strong>{' '}
              {t('management.gameDetection.depot', { count: game.depot_ids.length })}
            </span>
            {game.datasources && game.datasources.length > 0 && (
              <span className="flex items-center gap-1">
                {game.datasources.map((ds) => (
                  <span
                    key={ds}
                    className="px-1.5 py-0.5 rounded text-[10px] font-medium capitalize bg-themed-accent-subtle text-themed-accent"
                  >
                    {ds}
                  </span>
                ))}
              </span>
            )}
          </div>
        </div>
        <Tooltip content={t('management.gameDetection.removeGameCache')}>
          <Button
            onClick={() => onRemove(game)}
            disabled={isAnyRemovalRunning || !isAuthenticated || cacheReadOnly || !dockerSocketAvailable || checkingPermissions}
            variant="filled"
            color="red"
            size="sm"
            loading={isRemoving}
            title={
              cacheReadOnly
                ? t('management.gameDetection.cacheReadOnlyShort')
                : !dockerSocketAvailable
                  ? t('management.gameDetection.dockerSocketRequired')
                  : undefined
            }
          >
            {isRemoving ? t('management.gameDetection.removing') : t('common.remove')}
          </Button>
        </Tooltip>
      </div>

      {/* Loading State for Expansion */}
      {isExpanding && (
        <div className="border-t px-3 py-4 flex items-center justify-center border-themed-secondary">
          <div className="flex items-center gap-2 text-themed-muted">
            <Loader2 className="w-4 h-4 animate-spin" />
            <span className="text-sm">{t('management.gameDetection.loadingDetails')}</span>
          </div>
        </div>
      )}

      {/* Expandable Details Section */}
      {isExpanded && !isExpanding && (
        <div className="border-t px-3 py-3 space-y-3 border-themed-secondary">
          {/* Depot IDs */}
          {game.depot_ids.length > 0 && (
            <div>
              <p className="text-xs text-themed-muted mb-1.5 font-medium">{t('management.gameDetection.depotIds')}</p>
              <div className="flex flex-wrap gap-1">
                {game.depot_ids.map((depotId) => (
                  <span
                    key={depotId}
                    className="text-xs px-2 py-0.5 rounded border bg-themed-elevated border-themed-primary text-themed-secondary"
                  >
                    {depotId}
                  </span>
                ))}
              </div>
            </div>
          )}

          {/* Sample URLs */}
          {game.sample_urls.length > 0 && (
            <div>
              <div className="flex items-center justify-between mb-1.5">
                <p className="text-xs text-themed-muted font-medium">
                  {t('management.gameDetection.sampleUrls', { count: game.sample_urls.length })}
                </p>
                {game.sample_urls.length > MAX_INITIAL_URLS && (
                  <Button
                    variant="subtle"
                    size="xs"
                    onClick={() => setShowAllUrls(!showAllUrls)}
                    className="text-xs"
                  >
                    {showAllUrls ? t('management.gameDetection.showLess') : t('management.gameDetection.showAll', { count: game.sample_urls.length })}
                  </Button>
                )}
              </div>
              <div className="space-y-1 max-h-48 overflow-y-auto">
                {(showAllUrls ? game.sample_urls : game.sample_urls.slice(0, MAX_INITIAL_URLS)).map(
                  (url, idx) => (
                    <div
                      key={idx}
                      className="p-2 rounded border bg-themed-secondary border-themed-primary"
                    >
                      <Tooltip content={url}>
                        <span className="text-xs font-mono text-themed-primary truncate block">
                          {url}
                        </span>
                      </Tooltip>
                    </div>
                  )
                )}
              </div>
              {!showAllUrls && game.sample_urls.length > MAX_INITIAL_URLS && (
                <p className="text-xs text-themed-muted mt-2 italic">
                  {t('management.gameDetection.showingUrls', { showing: MAX_INITIAL_URLS, total: game.sample_urls.length })}
                </p>
              )}
            </div>
          )}

          {/* Cache File Paths */}
          {game.cache_file_paths && game.cache_file_paths.length > 0 && (
            <div>
              <div className="flex items-center justify-between mb-1.5">
                <p className="text-xs text-themed-muted font-medium">
                  {t('management.gameDetection.cacheFileLocations', { count: game.cache_file_paths.length })}
                </p>
                {game.cache_file_paths.length > MAX_INITIAL_PATHS && (
                  <Button
                    variant="subtle"
                    size="xs"
                    onClick={() => setShowAllPaths(!showAllPaths)}
                    className="text-xs"
                  >
                    {showAllPaths
                      ? t('management.gameDetection.showLess')
                      : t('management.gameDetection.showAll', { count: game.cache_file_paths.length })}
                  </Button>
                )}
              </div>
              <div className="space-y-1 max-h-48 overflow-y-auto">
                {(showAllPaths
                  ? game.cache_file_paths
                  : game.cache_file_paths.slice(0, MAX_INITIAL_PATHS)
                ).map((path, idx) => (
                  <div
                    key={idx}
                    className="p-2 rounded border bg-themed-secondary border-themed-primary"
                  >
                    <Tooltip content={path}>
                      <span className="text-xs font-mono text-themed-primary truncate block">
                        {path}
                      </span>
                    </Tooltip>
                  </div>
                ))}
              </div>
              {!showAllPaths && game.cache_file_paths.length > MAX_INITIAL_PATHS && (
                <p className="text-xs text-themed-muted mt-2 italic">
                  {t('management.gameDetection.showingPaths', { showing: MAX_INITIAL_PATHS, total: game.cache_file_paths.length })}
                </p>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
};

export default GameCard;
