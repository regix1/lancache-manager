import { useState, useMemo, useCallback, useEffect, ChangeEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { CustomScrollbar } from '../../ui/CustomScrollbar';
import { Search, Check, Gamepad2, Loader2, Import, Database, EyeOff, Eye } from 'lucide-react';

export interface OwnedGame {
  appId: string;
  name: string;
}

interface GameSelectionModalProps {
  opened: boolean;
  onClose: () => void;
  games: OwnedGame[];
  selectedAppIds: string[];
  onSave: (selectedIds: string[]) => Promise<void>;
  isLoading?: boolean;
  cachedAppIds?: string[];
  isUsingCache?: boolean;
  onRescan?: () => Promise<void>;
}

export function GameSelectionModal({
  opened,
  onClose,
  games,
  selectedAppIds,
  onSave,
  isLoading = false,
  cachedAppIds = [],
  isUsingCache = false,
  onRescan
}: GameSelectionModalProps) {
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  const [localSelected, setLocalSelected] = useState<Set<string>>(new Set());
  const [isSaving, setIsSaving] = useState(false);
  const [hideCached, setHideCached] = useState(false);

  // Create a Set for O(1) lookup
  const cachedAppIdsSet = useMemo(() => new Set(cachedAppIds), [cachedAppIds]);

  // Import state
  const [showImport, setShowImport] = useState(false);
  const [importText, setImportText] = useState('');
  const [importResult, setImportResult] = useState<{
    added: number;
    alreadySelected: number;
    notInLibrary: string[];
  } | null>(null);

  // Reset local selection when modal opens - start fresh each time
  useEffect(() => {
    if (opened) {
      // Start with current selection from parent, not cached
      setLocalSelected(new Set(selectedAppIds));
      setSearch('');
      setImportText('');
      setImportResult(null);
    }
  }, [opened, selectedAppIds]);

  // Parse import text - supports comma-separated, JSON array, or newline-separated
  const parseImportText = useCallback((text: string): string[] => {
    const trimmed = text.trim();
    if (!trimmed) return [];

    // Try JSON array first
    if (trimmed.startsWith('[')) {
      try {
        const parsed = JSON.parse(trimmed);
        if (Array.isArray(parsed)) {
          return parsed
            .map((id: string | number) => String(id))
            .filter((id: string) => id.length > 0);
        }
      } catch {
        // Fall through to other parsing methods
      }
    }

    // Split by comma, newline, or space
    return trimmed
      .split(/[,\n\s]+/)
      .map((s: string) => s.trim())
      .filter((id: string) => id.length > 0);
  }, []);

  // Handle import
  const handleImport = useCallback(() => {
    const appIds = parseImportText(importText);
    if (appIds.length === 0) {
      setImportResult({ added: 0, alreadySelected: 0, notInLibrary: [] });
      return;
    }

    const ownedAppIds = new Set(games.map(g => g.appId));
    let added = 0;
    let alreadySelected = 0;
    const notInLibrary: string[] = [];

    setLocalSelected(prev => {
      const next = new Set(prev);
      for (const appId of appIds) {
        if (!ownedAppIds.has(appId)) {
          notInLibrary.push(appId);
        } else if (next.has(appId)) {
          alreadySelected++;
        } else {
          next.add(appId);
          added++;
        }
      }
      return next;
    });

    setImportResult({ added, alreadySelected, notInLibrary });
    if (added > 0) {
      setImportText('');
    }
  }, [importText, games, parseImportText]);

  // Filter games by search and cached status
  const filteredGames = useMemo(() => {
    let filtered = games;

    // Filter by search
    if (search.trim()) {
      const searchLower = search.toLowerCase();
      filtered = filtered.filter(game =>
        game.name.toLowerCase().includes(searchLower) ||
        game.appId.toString().includes(search)
      );
    }

    // Filter out cached games if hideCached is enabled
    if (hideCached) {
      filtered = filtered.filter(game => !cachedAppIdsSet.has(game.appId));
    }

    return filtered;
  }, [games, search, hideCached, cachedAppIdsSet]);

  // Count cached games for display
  const cachedCount = useMemo(() =>
    games.filter(g => cachedAppIdsSet.has(g.appId)).length,
    [games, cachedAppIdsSet]
  );

  // Sort: selected first, then alphabetically
  const sortedGames = useMemo(() => {
    return [...filteredGames].sort((a, b) => {
      const aSelected = localSelected.has(a.appId);
      const bSelected = localSelected.has(b.appId);
      if (aSelected && !bSelected) return -1;
      if (!aSelected && bSelected) return 1;
      return a.name.localeCompare(b.name);
    });
  }, [filteredGames, localSelected]);

  const toggleGame = useCallback((appId: string) => {
    setLocalSelected(prev => {
      const next = new Set(prev);
      if (next.has(appId)) {
        next.delete(appId);
      } else {
        next.add(appId);
      }
      return next;
    });
  }, []);

  const selectAll = useCallback(() => {
    setLocalSelected(new Set(filteredGames.map(g => g.appId)));
  }, [filteredGames]);

  const selectNone = useCallback(() => {
    setLocalSelected(new Set());
  }, []);

  const handleSave = useCallback(async () => {
    setIsSaving(true);
    try {
      await onSave(Array.from(localSelected));
      onClose();
    } catch (err) {
      console.error('Failed to save selection:', err);
    } finally {
      setIsSaving(false);
    }
  }, [localSelected, onSave, onClose]);

  const handleRescan = useCallback(async () => {
    if (!onRescan) return;
    await onRescan();
  }, [onRescan]);

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t('prefill.gameSelection.title')}
      size="lg"
    >
      <div className="flex flex-col h-[70vh] sm:h-[60vh]">
        {/* Search and actions */}
        <div className="flex flex-col gap-3 mb-3">
          <div className="relative">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-[var(--theme-text-muted)]" />
            <input
              type="text"
              placeholder={t('prefill.placeholders.searchGames')}
              value={search}
              onChange={(e: ChangeEvent<HTMLInputElement>) => setSearch(e.target.value)}
              className="w-full pl-9 pr-3 py-2 text-sm rounded-lg smooth-transition bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)] text-[var(--theme-text-primary)] outline-none focus:border-[var(--theme-primary)] focus:ring-2 focus:ring-[var(--theme-primary)]/20"
            />
          </div>
          {isUsingCache && (
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 px-3 py-2 rounded-lg text-xs bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
              <div className="flex items-center gap-2 text-[var(--theme-text-muted)]">
                <Database className="h-3.5 w-3.5 text-[var(--theme-success)]" />
                <span>{t('prefill.gameSelection.usingCached')}</span>
              </div>
              {onRescan && (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={handleRescan}
                  disabled={isLoading}
                  className="h-7 px-2 text-xs"
                >
                  {t('prefill.gameSelection.rescan')}
                </Button>
              )}
            </div>
          )}
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
            <Button
              variant={showImport ? 'filled' : 'outline'}
              size="sm"
              onClick={() => setShowImport(!showImport)}
              fullWidth
            >
              <Import className="h-4 w-4" />
              {t('prefill.gameSelection.importAppIds')}
            </Button>
            {cachedCount > 0 && (
              <Button
                variant={hideCached ? 'filled' : 'outline'}
                size="sm"
                onClick={() => setHideCached(!hideCached)}
                title={hideCached ? t('prefill.gameSelection.showCachedTitle') : t('prefill.gameSelection.hideCachedTitle')}
                fullWidth
              >
                {hideCached ? <Eye className="h-4 w-4" /> : <EyeOff className="h-4 w-4" />}
                {hideCached ? t('prefill.gameSelection.showCached') : t('prefill.gameSelection.hideCached')}
              </Button>
            )}
            <Button variant="outline" size="sm" onClick={selectAll} fullWidth>
              {t('common.selectAll')}
            </Button>
            <Button variant="outline" size="sm" onClick={selectNone} fullWidth>
              {t('common.clear')}
            </Button>
          </div>
        </div>

        {/* Import Section - Expandable */}
        {showImport && (
          <div className="mb-3 p-3 rounded-lg bg-[var(--theme-bg-tertiary)] border border-dashed border-[var(--theme-primary)]">
            <p className="text-xs mb-2 text-[var(--theme-text-muted)]">
              {t('prefill.gameSelection.importHelp')}
            </p>
            <textarea
              value={importText}
              onChange={(e) => {
                setImportText(e.target.value);
                setImportResult(null);
              }}
              placeholder={t('prefill.placeholders.bulkInput')}
              className="w-full px-3 py-2 text-sm rounded-lg resize-none smooth-transition bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-secondary)] text-[var(--theme-text-primary)] outline-none min-h-[70px] focus:border-[var(--theme-primary)]"
            />
            <div className="flex flex-col sm:flex-row sm:items-center gap-2 mt-2">
              <Button 
                variant="filled" 
                size="sm" 
                onClick={handleImport}
                disabled={!importText.trim()}
              >
                <Import className="h-3.5 w-3.5" />
                {t('prefill.gameSelection.import')}
              </Button>
              <Button 
                variant="outline" 
                size="sm" 
                onClick={() => {
                  setShowImport(false);
                  setImportText('');
                  setImportResult(null);
                }}
              >
                {t('common.cancel')}
              </Button>
              {importResult && (
                <span className="text-xs sm:ml-auto text-[var(--theme-text-muted)]">
                  {importResult.added > 0 && (
                    <span className="text-[var(--theme-success)]">
                      {t('prefill.gameSelection.importAdded', { count: importResult.added })}
                    </span>
                  )}
                  {importResult.alreadySelected > 0 && (
                    <span>
                      {importResult.added > 0 ? ', ' : ''}
                      {t('prefill.gameSelection.importAlreadySelected', { count: importResult.alreadySelected })}
                    </span>
                  )}
                  {importResult.notInLibrary.length > 0 && (
                    <span className="text-[var(--theme-warning)]">
                      {(importResult.added > 0 || importResult.alreadySelected > 0) ? ', ' : ''}
                      {t('prefill.gameSelection.importNotInLibrary', { count: importResult.notInLibrary.length })}
                    </span>
                  )}
                  {importResult.added === 0 && importResult.alreadySelected === 0 && importResult.notInLibrary.length === 0 && (
                    <span className="text-[var(--theme-error)]">{t('prefill.gameSelection.noValidAppIds')}</span>
                  )}
                </span>
              )}
            </div>
          </div>
        )}

        {/* Selection count */}
        <div className="text-sm mb-2 text-[var(--theme-text-muted)] flex flex-wrap items-center gap-2">
          <span className="text-[var(--theme-primary)] font-semibold">{localSelected.size}</span>
          <span>{t('prefill.gameSelection.ofGamesSelected', { total: games.length, count: games.length })}</span>
          {cachedCount > 0 && (
            <span className="inline-flex items-center gap-1">
              <Database className="inline h-3.5 w-3.5 text-[var(--theme-success)]" />
              <span className="text-[var(--theme-success)]">{t('prefill.gameSelection.cached', { count: cachedCount })}</span>
            </span>
          )}
          {(search || hideCached) && (
            <span className="text-[var(--theme-text-muted)]">
              ({t('prefill.gameSelection.showing', { count: filteredGames.length })}{search ? ` ${t('prefill.gameSelection.matching', { query: search })}` : ''}{hideCached ? `, ${t('prefill.gameSelection.hidingCached')}` : ''})
            </span>
          )}
        </div>

        {/* Game list */}
        <div className="flex-1 relative rounded-lg overflow-hidden min-h-0 bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)]">
          {isLoading ? (
            <div className="flex items-center justify-center h-full">
              <Loader2 className="h-8 w-8 animate-spin text-[var(--theme-primary)]" />
            </div>
          ) : sortedGames.length === 0 ? (
            <div className="flex flex-col items-center justify-center h-full text-[var(--theme-text-muted)]">
              <div className="w-16 h-16 rounded-xl flex items-center justify-center mb-3 bg-[var(--theme-bg-secondary)]">
                <Gamepad2 className="h-8 w-8 opacity-50" />
              </div>
              <p className="font-medium">{t('prefill.gameSelection.noGamesFound')}</p>
              {search && (
                <p className="text-sm mt-1 opacity-70">{t('prefill.gameSelection.tryDifferentSearch')}</p>
              )}
            </div>
          ) : (
            <div className="absolute inset-0 flex flex-col overflow-hidden rounded-lg">
              {/* Selected games section - sticky at top */}
              {sortedGames.some(g => localSelected.has(g.appId)) && (
                <div
                  className={`flex flex-col ${
                    sortedGames.some(g => !localSelected.has(g.appId))
                      ? 'flex-shrink-0 max-h-[40%]'
                      : 'flex-1 min-h-0'
                  }`}
                >
                  <div
                    className="px-4 py-2 text-xs font-semibold uppercase tracking-wider flex-shrink-0 bg-[color-mix(in_srgb,var(--theme-primary)_15%,var(--theme-bg-tertiary))] text-[var(--theme-primary)] border-b border-[var(--theme-border-secondary)]"
                  >
                    {t('prefill.gameSelection.selected')}
                    {localSelected.size > 0 && <span className="count-badge">{localSelected.size}</span>}
                  </div>
                  <CustomScrollbar maxHeight="100%" className="flex-1 min-h-0" paddingMode="compact">
                    <div>
                      {sortedGames.filter(g => localSelected.has(g.appId)).map(game => {
                        const isCached = cachedAppIdsSet.has(game.appId);
                        return (
                          <button
                            key={game.appId}
                            onClick={() => toggleGame(game.appId)}
                            className="w-full flex items-center gap-3 px-4 py-3 text-left smooth-transition bg-[color-mix(in_srgb,var(--theme-primary)_10%,transparent)] border-b border-[var(--theme-border-secondary)] hover:bg-[color-mix(in_srgb,var(--theme-primary)_15%,transparent)]"
                          >
                            <div className="flex-shrink-0 w-5 h-5 rounded flex items-center justify-center bg-[var(--theme-primary)] border-2 border-[var(--theme-primary)]">
                              <Check className="h-3 w-3 text-white" />
                            </div>
                            <div className="flex-1 min-w-0">
                              <div className="truncate font-medium text-[var(--theme-text-primary)]">
                                {game.name}
                              </div>
                              <div className="text-xs text-[var(--theme-text-muted)] flex items-center gap-2">
                                <span>{t('prefill.gameSelection.appId', { id: game.appId })}</span>
                                {isCached && (
                                  <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium bg-[var(--theme-success)]/15 text-[var(--theme-success)]">
                                    <Database className="h-2.5 w-2.5" />
                                    {t('prefill.gameSelection.cachedBadge')}
                                  </span>
                                )}
                              </div>
                            </div>
                          </button>
                        );
                      })}
                    </div>
                  </CustomScrollbar>
                </div>
              )}

              {/* Available games section */}
              {sortedGames.some(g => !localSelected.has(g.appId)) && (
                <div className="flex-1 min-h-0 flex flex-col">
                  <div className="px-4 py-2 text-xs font-semibold uppercase tracking-wider flex-shrink-0 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-muted)] border-b border-[var(--theme-border-secondary)]">
                    {t('prefill.gameSelection.availableGames')}
                    <span className="count-badge">{sortedGames.filter(g => !localSelected.has(g.appId)).length}</span>
                  </div>
                  <CustomScrollbar maxHeight="100%" className="flex-1 min-h-0" paddingMode="compact">
                    <div>
                      {sortedGames.filter(g => !localSelected.has(g.appId)).map(game => {
                        const isCached = cachedAppIdsSet.has(game.appId);
                        return (
                          <button
                            key={game.appId}
                            onClick={() => toggleGame(game.appId)}
                            className="w-full flex items-center gap-3 px-4 py-3 text-left smooth-transition bg-transparent border-b border-[var(--theme-border-secondary)] hover:bg-[var(--theme-bg-hover)]"
                          >
                            <div className="flex-shrink-0 w-5 h-5 rounded flex items-center justify-center bg-transparent border-2 border-[var(--theme-border-primary)]" />
                            <div className="flex-1 min-w-0">
                              <div className="truncate font-medium text-[var(--theme-text-primary)]">
                                {game.name}
                              </div>
                              <div className="text-xs text-[var(--theme-text-muted)] flex items-center gap-2">
                                <span>{t('prefill.gameSelection.appId', { id: game.appId })}</span>
                                {isCached && (
                                  <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium bg-[var(--theme-success)]/15 text-[var(--theme-success)]">
                                    <Database className="h-2.5 w-2.5" />
                                    {t('prefill.gameSelection.cachedBadge')}
                                  </span>
                                )}
                              </div>
                            </div>
                          </button>
                        );
                      })}
                    </div>
                  </CustomScrollbar>
                </div>
              )}
            </div>
          )}
        </div>

        {/* Actions */}
        <div className="flex flex-col-reverse sm:flex-row sm:justify-end gap-2 mt-4 pt-4 border-t border-[var(--theme-border-secondary)]">
          <Button variant="outline" onClick={onClose} className="w-full sm:w-auto">
            {t('common.cancel')}
          </Button>
          <Button variant="filled" onClick={handleSave} disabled={isSaving} className="w-full sm:w-auto">
            {isSaving ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <Check className="h-4 w-4" />
            )}
            {t('prefill.gameSelection.saveSelection', { count: localSelected.size })}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
