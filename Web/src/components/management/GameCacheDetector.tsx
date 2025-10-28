import React, { useState, useEffect, useRef } from 'react';
import { HardDrive, Loader, Database, Trash2, AlertTriangle, ChevronDown, ChevronUp, FolderOpen, RefreshCw } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import type { GameCacheInfo } from '../../types';

interface GameCacheDetectorProps {
  mockMode?: boolean;
  isAuthenticated?: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}

const GameCacheDetector: React.FC<GameCacheDetectorProps> = ({
  mockMode = false,
  isAuthenticated = false,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  const [loading, setLoading] = useState(false);
  const [games, setGames] = useState<GameCacheInfo[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [totalGames, setTotalGames] = useState<number>(0);
  const [removingGameId, setRemovingGameId] = useState<number | null>(null);
  const [gameToRemove, setGameToRemove] = useState<GameCacheInfo | null>(null);
  const [expandedGameId, setExpandedGameId] = useState<number | null>(null);
  const [expandingGameId, setExpandingGameId] = useState<number | null>(null);
  const [showAllPaths, setShowAllPaths] = useState<Record<number, boolean>>({});
  const [showAllUrls, setShowAllUrls] = useState<Record<number, boolean>>({});
  const pollingIntervalRef = useRef<NodeJS.Timeout | null>(null);

  const MAX_INITIAL_PATHS = 50; // Only show 50 paths initially to prevent lag
  const MAX_INITIAL_URLS = 20; // Only show 20 URLs initially

  // Cleanup polling on unmount
  useEffect(() => {
    return () => {
      if (pollingIntervalRef.current) {
        clearInterval(pollingIntervalRef.current);
      }
    };
  }, []);

  // Check for active operations on mount
  useEffect(() => {
    const checkForActiveOperation = async () => {
      if (mockMode) return;

      try {
        const result = await ApiService.getActiveGameDetection();

        if (result.hasActiveOperation && result.operation) {
          // Resume polling for this operation
          setLoading(true);
          setError(null);

          if (pollingIntervalRef.current) {
            clearInterval(pollingIntervalRef.current);
          }

          pollingIntervalRef.current = setInterval(() => {
            pollDetectionStatus(result.operation!.operationId);
          }, 2000);

          // Poll immediately
          pollDetectionStatus(result.operation.operationId);
        }
      } catch (err) {
        console.error('Error checking for active operation:', err);
        // Don't show error to user - this is a background check
      }
    };

    checkForActiveOperation();
  }, [mockMode]); // Only run on mount or when mockMode changes

  const pollDetectionStatus = async (operationId: string) => {
    try {
      const status = await ApiService.getGameDetectionStatus(operationId);

      if (status.status === 'complete') {
        // Detection complete
        if (pollingIntervalRef.current) {
          clearInterval(pollingIntervalRef.current);
          pollingIntervalRef.current = null;
        }

        setLoading(false);

        if (status.games && status.totalGamesDetected !== undefined) {
          setGames(status.games);
          setTotalGames(status.totalGamesDetected);
          if (status.totalGamesDetected > 0) {
            onSuccess?.(`Detected ${status.totalGamesDetected} game${status.totalGamesDetected !== 1 ? 's' : ''} with cache files`);
          }
        }
      } else if (status.status === 'failed') {
        // Detection failed
        if (pollingIntervalRef.current) {
          clearInterval(pollingIntervalRef.current);
          pollingIntervalRef.current = null;
        }

        setLoading(false);
        const errorMsg = status.error || 'Detection failed';
        setError(errorMsg);
        onError?.(errorMsg);
      }
      // If status is 'running', continue polling
    } catch (err: any) {
      console.error('Error polling detection status:', err);
      // Continue polling even on error - might be temporary network issue
    }
  };

  const handleDetect = async () => {
    if (mockMode) {
      setError('Detection disabled in mock mode');
      onError?.('Detection disabled in mock mode');
      return;
    }

    setLoading(true);
    setError(null);
    setGames([]);
    setTotalGames(0);

    try {
      // Start background detection
      const result = await ApiService.startGameCacheDetection();

      // Start polling for status
      if (pollingIntervalRef.current) {
        clearInterval(pollingIntervalRef.current);
      }
      pollingIntervalRef.current = setInterval(() => {
        pollDetectionStatus(result.operationId);
      }, 2000); // Poll every 2 seconds

      // Poll immediately
      pollDetectionStatus(result.operationId);
    } catch (err: any) {
      const errorMsg = err.message || 'Failed to start game detection';
      setError(errorMsg);
      onError?.(errorMsg);
      console.error('Game detection error:', err);
      setLoading(false);
    }
  };

  const handleRemoveClick = (game: GameCacheInfo) => {
    if (!isAuthenticated) {
      onError?.('Full authentication required for management operations');
      return;
    }
    setGameToRemove(game);
  };

  const confirmRemoval = async () => {
    if (!gameToRemove) return;

    setRemovingGameId(gameToRemove.game_app_id);
    setError(null);

    try {
      const result = await ApiService.removeGameFromCache(gameToRemove.game_app_id);

      const message = `Removed ${result.report.game_name}: ${result.report.cache_files_deleted} cache files deleted, ${result.report.log_entries_removed} log entries removed, ${formatBytes(result.report.total_bytes_freed)} freed`;
      onSuccess?.(message);

      // Remove from the list
      setGames((prev) => prev.filter((g) => g.game_app_id !== gameToRemove.game_app_id));
      setTotalGames((prev) => prev - 1);

      // Trigger a refetch of all data to update Downloads tab
      onDataRefresh?.();

      setGameToRemove(null);
    } catch (err: any) {
      const errorMsg = err.message || 'Failed to remove game from cache';
      setError(errorMsg);
      onError?.(errorMsg);
      console.error('Game removal error:', err);
    } finally {
      setRemovingGameId(null);
    }
  };

  const toggleGameDetails = (gameId: number) => {
    // If already expanded, collapse immediately
    if (expandedGameId === gameId) {
      setExpandedGameId(null);
      setShowAllPaths(prev => ({ ...prev, [gameId]: false })); // Reset show all when collapsing
      setShowAllUrls(prev => ({ ...prev, [gameId]: false }));
      return;
    }

    // Show loading state for expansion
    setExpandingGameId(gameId);

    // Use setTimeout to allow the loading spinner to render before heavy DOM updates
    setTimeout(() => {
      setExpandedGameId(gameId);
      setExpandingGameId(null);
    }, 50); // Small delay to let spinner show
  };

  const toggleShowAllPaths = (gameId: number) => {
    setShowAllPaths(prev => ({ ...prev, [gameId]: !prev[gameId] }));
  };

  const toggleShowAllUrls = (gameId: number) => {
    setShowAllUrls(prev => ({ ...prev, [gameId]: !prev[gameId] }));
  };

  const formatBytes = (bytes: number): string => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${parseFloat((bytes / Math.pow(k, i)).toFixed(2))} ${sizes[i]}`;
  };

  return (
    <>
      <Card>
        <div className="space-y-4">
          {/* Header Section */}
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-lg font-semibold text-themed-primary flex items-center gap-2">
                <HardDrive className="w-5 h-5" />
                Game Cache Detection
              </h3>
              <p className="text-sm text-themed-secondary mt-1">
                Scan cache directory to find which games have stored files
              </p>
            </div>
            <Button
              onClick={handleDetect}
              disabled={loading || mockMode}
              variant="filled"
              color="blue"
              leftSection={loading ? <Loader className="w-4 h-4 animate-spin" /> : <Database className="w-4 h-4" />}
            >
              {loading ? 'Detecting...' : 'Detect Games'}
            </Button>
          </div>

          {/* Error Alert */}
          {error && !loading && (
            <Alert color="red">
              <div>
                <p className="text-sm font-medium mb-1">Failed to detect games in cache</p>
                <p className="text-xs opacity-75">{error}</p>
                <Button
                  variant="default"
                  size="sm"
                  onClick={handleDetect}
                  className="mt-2"
                  leftSection={<RefreshCw className="w-3 h-3" />}
                >
                  Try Again
                </Button>
              </div>
            </Alert>
          )}

          {/* Mock Mode Warning */}
          {mockMode && (
            <Alert color="yellow">
              Detection is disabled in mock mode
            </Alert>
          )}

          {/* Loading State */}
          {loading && (
            <div className="flex flex-col items-center justify-center py-8 gap-3">
              <Loader className="w-6 h-6 animate-spin text-themed-accent" />
              <p className="text-sm text-themed-secondary">Scanning database and cache directory...</p>
              <p className="text-xs text-themed-muted">This may take several minutes for large databases and cache directories</p>
            </div>
          )}

          {/* Games List */}
          {!loading && totalGames > 0 && (
            <>
              <div className="mb-3 p-3 rounded-lg border" style={{
                backgroundColor: 'var(--theme-bg-elevated)',
                borderColor: 'var(--theme-border-secondary)'
              }}>
                <div className="flex items-center gap-2 text-themed-primary font-medium">
                  <Database className="w-5 h-5 text-themed-accent" />
                  Found {totalGames} game{totalGames !== 1 ? 's' : ''} with cache files
                </div>
              </div>

              <div className="space-y-3">
                {games.map((game) => (
                  <div
                    key={game.game_app_id}
                    className="rounded-lg border"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      borderColor: 'var(--theme-border-secondary)'
                    }}
                  >
                    <div className="flex items-center gap-2 p-3">
                      <Button
                        onClick={() => toggleGameDetails(game.game_app_id)}
                        variant="subtle"
                        size="sm"
                        className="flex-shrink-0"
                        disabled={!!removingGameId || expandingGameId === game.game_app_id}
                      >
                        {expandingGameId === game.game_app_id ? (
                          <Loader className="w-4 h-4 animate-spin" />
                        ) : expandedGameId === game.game_app_id ? (
                          <ChevronUp className="w-4 h-4" />
                        ) : (
                          <ChevronDown className="w-4 h-4" />
                        )}
                      </Button>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center gap-2 mb-1">
                          <h4 className="text-themed-primary font-semibold truncate">
                            {game.game_name}
                          </h4>
                          <span className="text-xs text-themed-muted bg-themed-elevated px-2 py-0.5 rounded flex-shrink-0">
                            AppID: {game.game_app_id}
                          </span>
                        </div>
                        <div className="flex items-center gap-3 text-xs text-themed-muted flex-wrap">
                          <span className="flex items-center gap-1">
                            <FolderOpen className="w-3 h-3" />
                            <strong className="text-themed-primary">{game.cache_files_found.toLocaleString()}</strong> files
                          </span>
                          <span className="flex items-center gap-1">
                            <HardDrive className="w-3 h-3" />
                            <strong className="text-themed-primary">{formatBytes(game.total_size_bytes)}</strong>
                          </span>
                          <span className="flex items-center gap-1">
                            <Database className="w-3 h-3" />
                            <strong className="text-themed-primary">{game.depot_ids.length}</strong> depot{game.depot_ids.length !== 1 ? 's' : ''}
                          </span>
                        </div>
                      </div>
                      <Tooltip content="Remove all cache files for this game">
                        <Button
                          onClick={() => handleRemoveClick(game)}
                          disabled={mockMode || removingGameId === game.game_app_id || !isAuthenticated}
                          variant="filled"
                          color="red"
                          size="sm"
                          loading={removingGameId === game.game_app_id}
                        >
                          {removingGameId !== game.game_app_id ? 'Remove' : 'Removing...'}
                        </Button>
                      </Tooltip>
                    </div>

                    {/* Loading State for Expansion */}
                    {expandingGameId === game.game_app_id && (
                      <div className="border-t px-3 py-4 flex items-center justify-center" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                        <div className="flex items-center gap-2 text-themed-muted">
                          <Loader className="w-4 h-4 animate-spin" />
                          <span className="text-sm">Loading details...</span>
                        </div>
                      </div>
                    )}

                    {/* Expandable Details Section */}
                    {expandedGameId === game.game_app_id && expandingGameId !== game.game_app_id && (
                      <div className="border-t px-3 py-3 space-y-3" style={{ borderColor: 'var(--theme-border-secondary)' }}>
                        {/* Depot IDs */}
                        {game.depot_ids.length > 0 && (
                          <div>
                            <p className="text-xs text-themed-muted mb-1.5 font-medium">Depot IDs:</p>
                            <div className="flex flex-wrap gap-1">
                              {game.depot_ids.map((depotId) => (
                                <span
                                  key={depotId}
                                  className="text-xs px-2 py-0.5 rounded border"
                                  style={{
                                    backgroundColor: 'var(--theme-bg-elevated)',
                                    borderColor: 'var(--theme-border-primary)',
                                    color: 'var(--theme-text-secondary)'
                                  }}
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
                                Sample URLs ({game.sample_urls.length}):
                              </p>
                              {game.sample_urls.length > MAX_INITIAL_URLS && (
                                <Button
                                  variant="subtle"
                                  size="xs"
                                  onClick={() => toggleShowAllUrls(game.game_app_id)}
                                  className="text-xs"
                                >
                                  {showAllUrls[game.game_app_id]
                                    ? `Show less`
                                    : `Show all ${game.sample_urls.length}`
                                  }
                                </Button>
                              )}
                            </div>
                            <div className="space-y-1 max-h-48 overflow-y-auto">
                              {(showAllUrls[game.game_app_id]
                                ? game.sample_urls
                                : game.sample_urls.slice(0, MAX_INITIAL_URLS)
                              ).map((url, idx) => (
                                <div
                                  key={idx}
                                  className="p-2 rounded border"
                                  style={{
                                    backgroundColor: 'var(--theme-bg-secondary)',
                                    borderColor: 'var(--theme-border-primary)'
                                  }}
                                >
                                  <Tooltip content={url}>
                                    <span className="text-xs font-mono text-themed-primary truncate block">
                                      {url}
                                    </span>
                                  </Tooltip>
                                </div>
                              ))}
                            </div>
                            {!showAllUrls[game.game_app_id] && game.sample_urls.length > MAX_INITIAL_URLS && (
                              <p className="text-xs text-themed-muted mt-2 italic">
                                Showing {MAX_INITIAL_URLS} of {game.sample_urls.length} URLs
                              </p>
                            )}
                          </div>
                        )}

                        {/* Cache File Paths */}
                        {game.cache_file_paths && game.cache_file_paths.length > 0 && (
                          <div>
                            <div className="flex items-center justify-between mb-1.5">
                              <p className="text-xs text-themed-muted font-medium">
                                Cache File Locations ({game.cache_file_paths.length.toLocaleString()}):
                              </p>
                              {game.cache_file_paths.length > MAX_INITIAL_PATHS && (
                                <Button
                                  variant="subtle"
                                  size="xs"
                                  onClick={() => toggleShowAllPaths(game.game_app_id)}
                                  className="text-xs"
                                >
                                  {showAllPaths[game.game_app_id]
                                    ? `Show less`
                                    : `Show all ${game.cache_file_paths.length.toLocaleString()}`
                                  }
                                </Button>
                              )}
                            </div>
                            <div className="space-y-1 max-h-48 overflow-y-auto">
                              {(showAllPaths[game.game_app_id]
                                ? game.cache_file_paths
                                : game.cache_file_paths.slice(0, MAX_INITIAL_PATHS)
                              ).map((path, idx) => (
                                <div
                                  key={idx}
                                  className="p-2 rounded border"
                                  style={{
                                    backgroundColor: 'var(--theme-bg-secondary)',
                                    borderColor: 'var(--theme-border-primary)'
                                  }}
                                >
                                  <Tooltip content={path}>
                                    <span className="text-xs font-mono text-themed-primary truncate block">
                                      {path}
                                    </span>
                                  </Tooltip>
                                </div>
                              ))}
                            </div>
                            {!showAllPaths[game.game_app_id] && game.cache_file_paths.length > MAX_INITIAL_PATHS && (
                              <p className="text-xs text-themed-muted mt-2 italic">
                                Showing {MAX_INITIAL_PATHS} of {game.cache_file_paths.length.toLocaleString()} paths
                              </p>
                            )}
                          </div>
                        )}
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </>
          )}

          {/* Empty State */}
          {!loading && totalGames === 0 && games.length === 0 && !error && (
            <div className="text-center py-8 text-themed-muted">
              <HardDrive className="w-12 h-12 mx-auto mb-3 opacity-50" />
              <div className="mb-2">No games with cache files detected</div>
              <div className="text-xs">
                Click "Detect Games" to scan your cache directory
              </div>
            </div>
          )}

          {/* Information Alert */}
          <Alert color="blue">
            <div>
              <p className="text-xs font-medium mb-2">About Game Cache Detection:</p>
              <ul className="list-disc list-inside text-xs space-y-1 ml-2">
                <li>Scans database for game records and checks if cache files exist</li>
                <li>Shows total cache size and file count per game</li>
                <li>Removal deletes ALL cache files for the selected game</li>
                <li>Log entries are preserved for analytics (use Corruption Removal to delete logs)</li>
              </ul>
            </div>
          </Alert>
        </div>
      </Card>

      {/* Confirmation Modal */}
      <Modal
        opened={gameToRemove !== null}
        onClose={() => !removingGameId && setGameToRemove(null)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Remove Game from Cache</span>
          </div>
        }
      >
        {gameToRemove && (
          <div className="space-y-4">
            <p className="text-themed-secondary">
              Are you sure you want to remove <span className="font-semibold text-themed-primary">{gameToRemove.game_name}</span> from cache?
            </p>

            <Alert color="yellow">
              <div>
                <p className="text-xs font-medium mb-2">This will:</p>
                <ul className="list-disc list-inside text-xs space-y-1 ml-2">
                  <li>Delete approximately {gameToRemove.cache_files_found.toLocaleString()} cache files</li>
                  <li>Free up approximately {formatBytes(gameToRemove.total_size_bytes)}</li>
                  <li>Remove cache for {gameToRemove.depot_ids.length} depot{gameToRemove.depot_ids.length !== 1 ? 's' : ''}</li>
                  <li>This action cannot be undone</li>
                </ul>
              </div>
            </Alert>

            <div className="flex justify-end space-x-3 pt-2">
              <Button
                variant="default"
                onClick={() => setGameToRemove(null)}
                disabled={removingGameId !== null}
              >
                Cancel
              </Button>
              <Button
                variant="filled"
                color="red"
                leftSection={!removingGameId && <Trash2 className="w-4 h-4" />}
                onClick={confirmRemoval}
                loading={removingGameId !== null}
              >
                Remove from Cache
              </Button>
            </div>
          </div>
        )}
      </Modal>
    </>
  );
};

export default GameCacheDetector;
