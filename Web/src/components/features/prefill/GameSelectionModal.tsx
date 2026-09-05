import { noAutofill } from '@utils/autofill';
import { useState, useMemo, useCallback, useEffect, type ChangeEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { Tooltip } from '../../ui/Tooltip';
import Badge from '../../ui/Badge';
import { CollapsibleRegion } from '../../ui/CollapsibleRegion';
import { CustomScrollbar } from '../../ui/CustomScrollbar';
import { SearchInput } from '../../ui/SearchInput';
import { Check, Gamepad2, Import, Database, Trash2 } from 'lucide-react';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { EmptyState } from '@components/ui/ManagerCard';
import { useErrorHandler } from '@hooks/useErrorHandler';

export interface OwnedGame {
  appId: string;
  name: string;
}

interface GameSelectionModalProps {
  opened: boolean;
  onClose: () => void;
  // Which platform's library is on screen. Only Steam gets the App ID import, whose IDs and
  // help text come from SteamPrefill and mean nothing on Epic, Xbox, Battle.net or Riot.
  serviceId: string;
  games: OwnedGame[];
  selectedAppIds: string[];
  onSave: (selectedIds: string[]) => Promise<void>;
  isLoading?: boolean;
  cachedAppIds?: string[];
  isUsingCache?: boolean;
  onRescan?: () => Promise<void>;
  onRemoveFromCache?: (appId: string) => Promise<void>;
  removingAppId?: string | null;
}

export function GameSelectionModal({
  opened,
  onClose,
  serviceId,
  games,
  selectedAppIds,
  onSave,
  isLoading = false,
  cachedAppIds = [],
  isUsingCache = false,
  onRescan,
  onRemoveFromCache,
  removingAppId = null
}: GameSelectionModalProps) {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
  const [search, setSearch] = useState('');
  const [localSelected, setLocalSelected] = useState<Set<string>>(new Set());
  const [isSaving, setIsSaving] = useState(false);
  const [hideCached, setHideCached] = useState(false);

  // Create a Set for O(1) lookup
  const cachedAppIdsSet = useMemo(() => new Set(cachedAppIds), [cachedAppIds]);
  const gameIdSet = useMemo(() => new Set(games.map((g) => g.appId)), [games]);

  const canImportAppIds = serviceId === 'steam';

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

    let added = 0;
    let alreadySelected = 0;
    const notInLibrary: string[] = [];

    setLocalSelected((prev) => {
      const next = new Set(prev);
      for (const appId of appIds) {
        if (!gameIdSet.has(appId)) {
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
  }, [importText, gameIdSet, parseImportText]);

  // Filter games by search. The cached filter belongs to the available group alone: a game the
  // user has already picked has to stay on screen or there is no way left to untick it.
  const filteredGames = useMemo(() => {
    let filtered = games;

    // Filter by search
    if (search.trim()) {
      const searchLower = search.toLowerCase();
      filtered = filtered.filter(
        (game) =>
          game.name.toLowerCase().includes(searchLower) || game.appId.toString().includes(search)
      );
    }

    return filtered;
  }, [games, search]);

  // Count cached games for display
  const cachedCount = useMemo(
    () => games.filter((g) => cachedAppIdsSet.has(g.appId)).length,
    [games, cachedAppIdsSet]
  );

  // The parent's selection can name an app the library no longer lists. A stale id would
  // otherwise inflate every count here and be handed straight back to the server on save.
  const selectedInLibrary = useMemo(
    () => [...localSelected].filter((id) => gameIdSet.has(id)),
    [localSelected, gameIdSet]
  );

  const cachedSelectedCount = useMemo(
    () => selectedInLibrary.filter((id) => cachedAppIdsSet.has(id)).length,
    [selectedInLibrary, cachedAppIdsSet]
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

  const selectedGames = useMemo(
    () => sortedGames.filter((game) => localSelected.has(game.appId)),
    [sortedGames, localSelected]
  );

  // hideCached applies to this group only, so a selected game that is already cached keeps a row.
  const availableGames = useMemo(
    () =>
      sortedGames.filter(
        (game) => !localSelected.has(game.appId) && !(hideCached && cachedAppIdsSet.has(game.appId))
      ),
    [sortedGames, localSelected, hideCached, cachedAppIdsSet]
  );

  const shownGamesCount = selectedGames.length + availableGames.length;

  // Five reasons the list can come up empty, first match wins. The search-with-a-selection branch
  // comes before the plain search ones because a selection the search has hidden is the one case
  // where the header still counts games the reader can see no row for. The last one names the
  // toggle by its on-screen label, which is why that label no longer flips between Show and Hide.
  const emptyState = useMemo(() => {
    if (games.length === 0) {
      return {
        title: t('prefill.gameSelection.libraryEmpty'),
        subtitle: t('prefill.gameSelection.libraryEmptyHelp')
      };
    }
    if (search.trim() && selectedInLibrary.length > 0) {
      return {
        title: t('prefill.gameSelection.noGamesFound'),
        subtitle: t('prefill.gameSelection.selectedHiddenBySearch', {
          count: selectedInLibrary.length
        })
      };
    }
    if (search.trim() && hideCached) {
      return {
        title: t('prefill.gameSelection.noGamesFound'),
        subtitle: t('prefill.gameSelection.tryDifferentSearchOrShowCached')
      };
    }
    if (search.trim()) {
      return {
        title: t('prefill.gameSelection.noGamesFound'),
        subtitle: t('prefill.gameSelection.tryDifferentSearch')
      };
    }
    return {
      title: t('prefill.gameSelection.allCachedHidden'),
      subtitle: t('prefill.gameSelection.allCachedHiddenHelp')
    };
  }, [games.length, search, hideCached, selectedInLibrary.length, t]);

  const toggleGame = useCallback((appId: string) => {
    setLocalSelected((prev) => {
      const next = new Set(prev);
      if (next.has(appId)) {
        next.delete(appId);
      } else {
        next.add(appId);
      }
      return next;
    });
  }, []);

  // The rows on screen, both groups: hiding cached games has to keep them out of Select All too,
  // or the button hands back a selection the user asked not to see.
  const selectAll = useCallback(() => {
    setLocalSelected(new Set([...selectedGames, ...availableGames].map((g) => g.appId)));
  }, [selectedGames, availableGames]);

  const selectNone = useCallback(() => {
    setLocalSelected(new Set());
  }, []);

  const handleSave = useCallback(async () => {
    setIsSaving(true);
    try {
      // An empty library means the games route answered with nothing, not that every pick is an
      // orphan. Filtering against it would post an empty list and erase the whole selection.
      await onSave(games.length > 0 ? selectedInLibrary : Array.from(localSelected));
      onClose();
    } catch (err) {
      notifyError(t('prefill.errors.saveSelectionFailed'), err, {
        logLabel: 'Failed to save selection'
      });
    } finally {
      setIsSaving(false);
    }
  }, [games.length, selectedInLibrary, localSelected, onSave, onClose, notifyError, t]);

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t('prefill.gameSelection.title')}
      size="lg"
      bodyFlexLayout
    >
      <div className="flex h-[calc(100dvh-8rem)] min-h-[34rem] max-h-[calc(100dvh-8rem)] sm:max-h-[40rem] flex-col">
        {/* Search and actions */}
        <div className="flex flex-col gap-2 mb-2">
          <SearchInput
            placeholder={t('prefill.placeholders.searchGames')}
            value={search}
            onChange={(e: ChangeEvent<HTMLInputElement>) => setSearch(e.target.value)}
            onClear={() => setSearch('')}
          />
          {/* Wrapping row rather than a 4-column grid: the cluster holds three to five buttons
              depending on Import and Rescan, and a grid leaves the short last row half empty. */}
          <div className="flex flex-wrap gap-2">
            {canImportAppIds && (
              <Button
                variant="filled"
                color={showImport ? 'primary' : 'secondary'}
                size="sm"
                onClick={() => setShowImport(!showImport)}
                className="flex-1 basis-[calc(50%-0.25rem)] min-[560px]:basis-0 min-[560px]:min-w-[6rem] min-h-[44px] sm:min-h-8"
              >
                <Import className="h-4 w-4" />
                {t('prefill.gameSelection.importAppIds')}
              </Button>
            )}
            {cachedCount > 0 && (
              <Button
                variant="filled"
                color={hideCached ? 'secondary' : 'primary'}
                size="sm"
                onClick={() => setHideCached(!hideCached)}
                aria-pressed={!hideCached}
                className="flex-1 basis-[calc(50%-0.25rem)] min-[560px]:basis-0 min-[560px]:min-w-[6rem] min-h-[44px] sm:min-h-8"
              >
                {t('prefill.gameSelection.showCached')}
              </Button>
            )}
            {onRescan && (
              <Button
                variant="filled"
                color="run"
                size="sm"
                onClick={onRescan}
                disabled={isLoading}
                className="flex-1 basis-[calc(50%-0.25rem)] min-[560px]:basis-0 min-[560px]:min-w-[6rem] min-h-[44px] sm:min-h-8"
              >
                {t('prefill.gameSelection.rescan')}
              </Button>
            )}
            <Button
              variant="filled"
              color="secondary"
              size="sm"
              onClick={selectAll}
              className="flex-1 basis-[calc(50%-0.25rem)] min-[560px]:basis-0 min-[560px]:min-w-[6rem] min-h-[44px] sm:min-h-8"
            >
              {t('common.selectAll')}
            </Button>
            <Button
              variant="filled"
              color="secondary"
              size="sm"
              onClick={selectNone}
              className="flex-1 basis-[calc(50%-0.25rem)] min-[560px]:basis-0 min-[560px]:min-w-[6rem] min-h-[44px] sm:min-h-8"
            >
              {t('common.clear')}
            </Button>
          </div>
        </div>

        {/* Import Section - Expandable */}
        <CollapsibleRegion open={canImportAppIds && showImport}>
          <div className="mb-2 p-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-dashed border-[var(--theme-primary)]">
            <p className="text-xs mb-1 text-[var(--theme-text-muted)]">
              {t('prefill.gameSelection.importHelp')}
            </p>
            <textarea
              {...noAutofill}
              value={importText}
              onChange={(e) => {
                setImportText(e.target.value);
                setImportResult(null);
              }}
              placeholder={t('prefill.placeholders.bulkInput')}
              className="w-full px-3 py-2 text-sm rounded-lg resize-none transition-[border-color] duration-150 ease-out bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-secondary)] text-[var(--theme-text-primary)] focus:outline-none focus:border-[var(--theme-primary)] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--theme-border-focus)] min-h-[52px]"
            />
            <div className="flex flex-col sm:flex-row sm:items-center gap-2 mt-1">
              <Button
                variant="filled"
                color="run"
                size="sm"
                onClick={handleImport}
                disabled={!importText.trim()}
              >
                <Import className="h-3.5 w-3.5" />
                {t('prefill.gameSelection.import')}
              </Button>
              <Button
                variant="filled"
                color="secondary"
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
                      {t('prefill.gameSelection.importAlreadySelected', {
                        count: importResult.alreadySelected
                      })}
                    </span>
                  )}
                  {importResult.notInLibrary.length > 0 && (
                    <span className="text-[var(--theme-warning)]">
                      {importResult.added > 0 || importResult.alreadySelected > 0 ? ', ' : ''}
                      {t('prefill.gameSelection.importNotInLibrary', {
                        count: importResult.notInLibrary.length
                      })}
                    </span>
                  )}
                  {importResult.added === 0 &&
                    importResult.alreadySelected === 0 &&
                    importResult.notInLibrary.length === 0 && (
                      <span className="text-[var(--theme-error)]">
                        {t('prefill.gameSelection.noValidAppIds')}
                      </span>
                    )}
                </span>
              )}
            </div>
          </div>
        </CollapsibleRegion>

        {/* Library/filter facts only. The selected count lives once, in the Selected section
            header below, so it is not repeated here. */}
        <div className="text-sm mb-1 text-[var(--theme-text-muted)] flex flex-wrap items-center gap-2">
          {isUsingCache && (
            <span className="inline-flex items-center gap-1 text-xs">
              <Database className="h-3.5 w-3.5 text-[var(--theme-success)]" />
              {t('prefill.gameSelection.usingCached')}
            </span>
          )}
          {shownGamesCount !== games.length ? (
            <span className="tabular-nums">
              {t('prefill.gameSelection.showingOfTotal', {
                count: shownGamesCount,
                total: games.length
              })}
            </span>
          ) : (
            <span className="tabular-nums">
              {t('prefill.gameSelection.libraryTotal', { count: games.length })}
            </span>
          )}
        </div>

        {/* Game list */}
        <div className="relative min-h-40 sm:min-h-[15rem] flex-1 overflow-hidden rounded-lg border border-[var(--theme-border-secondary)] bg-[var(--theme-bg-tertiary)]">
          {isLoading ? (
            <div className="p-3 space-y-2" aria-busy="true">
              {[0, 1, 2, 3, 4].map((row) => (
                <div key={row} className="flex items-center gap-3 px-4 py-3">
                  <div className="flex-shrink-0 w-5 h-5 rounded skeleton-shimmer" />
                  <div className="flex-1 min-w-0 space-y-1.5">
                    <div className="skeleton-shimmer h-4 rounded w-2/5" />
                    <div className="skeleton-shimmer h-3 rounded w-1/5" />
                  </div>
                </div>
              ))}
            </div>
          ) : shownGamesCount === 0 ? (
            <div className="flex h-full items-center justify-center">
              <EmptyState
                variant="panel"
                icon={Gamepad2}
                title={emptyState.title}
                subtitle={emptyState.subtitle}
              />
            </div>
          ) : (
            <div className="absolute inset-0 flex flex-col overflow-hidden rounded-lg">
              {/* Selected games section - sticky at top */}
              {selectedGames.length > 0 && (
                <div
                  className={`flex flex-col ${
                    availableGames.length > 0 ? 'h-24 sm:h-20 flex-none' : 'flex-1 min-h-0'
                  }`}
                >
                  <div className="px-4 py-2 text-xs font-semibold uppercase tracking-wider flex-shrink-0 flex items-center gap-2 bg-[var(--theme-selected-bg)] text-[var(--theme-selected-text)] border-b border-[var(--theme-border-secondary)]">
                    <span>{t('prefill.gameSelection.selected')}</span>
                    <Badge variant="neutral" className="badge-count">
                      {selectedInLibrary.length}
                    </Badge>
                    {/* Only worth stating when the counts diverge; with nothing cached the badge
                        already says how many will download. "Already cached" stays muted: those
                        are the games the daemon will skip, neutral news rather than good news. */}
                    {cachedSelectedCount > 0 && (
                      <span className="normal-case font-normal tracking-normal tabular-nums text-[var(--theme-text-muted)]">
                        {t('prefill.gameSelection.willDownload', {
                          count: selectedInLibrary.length - cachedSelectedCount
                        })}{' '}
                        ·{' '}
                        {t('prefill.gameSelection.alreadyCachedCount', {
                          count: cachedSelectedCount
                        })}
                      </span>
                    )}
                  </div>
                  <CustomScrollbar
                    maxHeight="100%"
                    className="flex-1 min-h-0"
                    paddingMode="compact"
                    radius="none"
                    variant="float"
                  >
                    <div className="divided-list">
                      {selectedGames.map((game) => {
                        const isCached = cachedAppIdsSet.has(game.appId);
                        return (
                          /* The row carries the background so the strip behind the remove control
                             is painted too, and so the control is a sibling of the select toggle
                             rather than a button nested inside one. */
                          <div
                            key={game.appId}
                            className="w-full flex items-center min-h-[44px] transition-[background-color] duration-150 ease-out bg-[var(--theme-selected-bg)] hover:bg-[var(--theme-selected-bg-hover)]"
                          >
                            <Button
                              type="button"
                              variant="transparent"
                              onClick={() => toggleGame(game.appId)}
                              /* Square corners: the row paints its own selected background, and
                                 the button's default radius would leave the list's darker
                                 surface showing through all four corners of every row. */
                              className="flex-1 min-w-0 !rounded-none flex items-center gap-3 px-4 !py-0.5 text-left bg-transparent hover:bg-transparent focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-[-2px] focus-visible:outline-[var(--theme-border-focus)]"
                            >
                              <div className="flex-shrink-0 w-5 h-5 rounded flex items-center justify-center bg-[var(--theme-primary)] border-2 border-[var(--theme-primary)]">
                                <Check className="h-3 w-3 text-[var(--theme-button-text)]" />
                              </div>
                              <div className="flex-1 min-w-0">
                                <Tooltip
                                  content={game.name}
                                  position="top"
                                  className="block min-w-0"
                                >
                                  <div className="truncate font-medium text-[var(--theme-text-primary)]">
                                    {game.name}
                                  </div>
                                </Tooltip>
                                <div className="text-xs text-[var(--theme-text-muted)] flex items-center gap-2">
                                  <span className="min-w-0 truncate">
                                    {t('prefill.gameSelection.appId', { id: game.appId })}
                                  </span>
                                  {isCached && (
                                    <Badge variant="success">
                                      {t('prefill.gameSelection.cachedBadge')}
                                    </Badge>
                                  )}
                                </div>
                              </div>
                            </Button>
                            {isCached && onRemoveFromCache && (
                              <Button
                                type="button"
                                variant="filled"
                                color="secondary"
                                size="sm"
                                onClick={() => onRemoveFromCache(game.appId)}
                                disabled={removingAppId === game.appId}
                                aria-label={t('prefill.gameSelection.removeFromCache', {
                                  name: game.name
                                })}
                                /* Both margins are load-bearing: .pointer-target-44 overhangs the
                                   32px box by 6px a side. The right 6px have to land inside mr-3
                                   rather than across the list's border, and without ml-2 the left
                                   6px sit over the select toggle, where a tap meant to untick the
                                   row removes it from the cache instead (measured at 320 and 390). */
                                className="btn-icon-square btn-icon-square--sm pointer-target-44 delete-hover ml-2 mr-3 flex-shrink-0"
                              >
                                <Trash2 className="w-4 h-4" />
                              </Button>
                            )}
                          </div>
                        );
                      })}
                    </div>
                  </CustomScrollbar>
                </div>
              )}

              {/* Available games section */}
              {availableGames.length > 0 && (
                <div className="flex-1 min-h-0 flex flex-col">
                  <div className="px-4 py-2 text-xs font-semibold uppercase tracking-wider flex-shrink-0 flex items-center gap-2 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-muted)] border-b border-[var(--theme-border-secondary)]">
                    <span>{t('prefill.gameSelection.availableGames')}</span>
                    <Badge variant="neutral" className="badge-count">
                      {availableGames.length}
                    </Badge>
                  </div>
                  <CustomScrollbar
                    maxHeight="100%"
                    className="flex-1 min-h-0"
                    paddingMode="compact"
                    radius="none"
                    variant="float"
                  >
                    <div className="divided-list">
                      {availableGames.map((game) => {
                        const isCached = cachedAppIdsSet.has(game.appId);
                        return (
                          <div
                            key={game.appId}
                            className="w-full flex items-center min-h-[44px] transition-[background-color] duration-150 ease-out bg-transparent hover:bg-[var(--theme-bg-hover)]"
                          >
                            <Button
                              type="button"
                              variant="transparent"
                              onClick={() => toggleGame(game.appId)}
                              className="flex-1 min-w-0 !rounded-none flex items-center gap-3 px-4 !py-0.5 text-left bg-transparent hover:bg-transparent focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-[-2px] focus-visible:outline-[var(--theme-border-focus)]"
                            >
                              <div className="flex-shrink-0 w-5 h-5 rounded flex items-center justify-center bg-transparent border-2 border-[var(--theme-border-primary)]" />
                              <div className="flex-1 min-w-0">
                                <Tooltip
                                  content={game.name}
                                  position="top"
                                  className="block min-w-0"
                                >
                                  <div className="truncate font-medium text-[var(--theme-text-primary)]">
                                    {game.name}
                                  </div>
                                </Tooltip>
                                <div className="text-xs text-[var(--theme-text-muted)] flex items-center gap-2">
                                  <span className="min-w-0 truncate">
                                    {t('prefill.gameSelection.appId', { id: game.appId })}
                                  </span>
                                  {isCached && (
                                    <Badge variant="success">
                                      {t('prefill.gameSelection.cachedBadge')}
                                    </Badge>
                                  )}
                                </div>
                              </div>
                            </Button>
                            {isCached && onRemoveFromCache && (
                              <Button
                                type="button"
                                variant="filled"
                                color="secondary"
                                size="sm"
                                onClick={() => onRemoveFromCache(game.appId)}
                                disabled={removingAppId === game.appId}
                                aria-label={t('prefill.gameSelection.removeFromCache', {
                                  name: game.name
                                })}
                                className="btn-icon-square btn-icon-square--sm pointer-target-44 delete-hover ml-2 mr-3 flex-shrink-0"
                              >
                                <Trash2 className="w-4 h-4" />
                              </Button>
                            )}
                          </div>
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
        <div className="flex flex-row justify-end gap-2 mt-2 pt-2 sm:pt-3 border-t border-[var(--theme-border-secondary)]">
          <Button
            variant="filled"
            color="secondary"
            onClick={onClose}
            className="flex-1 sm:flex-none sm:w-auto min-h-[44px] sm:min-h-10"
          >
            {t('common.cancel')}
          </Button>
          <Button
            variant="filled"
            color="primary"
            onClick={handleSave}
            disabled={isSaving}
            className="flex-1 sm:flex-none sm:w-auto min-h-[44px] sm:min-h-10"
          >
            {isSaving ? <LoadingSpinner inline size="sm" /> : <Check className="h-4 w-4" />}
            {t('prefill.gameSelection.saveSelection')}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
