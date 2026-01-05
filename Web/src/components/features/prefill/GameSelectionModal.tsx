import { useState, useMemo, useCallback, useEffect, ChangeEvent } from 'react';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { CustomScrollbar } from '../../ui/CustomScrollbar';
import { Search, Check, Gamepad2, Loader2, Import, ChevronDown } from 'lucide-react';

export interface OwnedGame {
  appId: number;
  name: string;
}

interface GameSelectionModalProps {
  opened: boolean;
  onClose: () => void;
  games: OwnedGame[];
  selectedAppIds: number[];
  onSave: (selectedIds: number[]) => Promise<void>;
  isLoading?: boolean;
}

export function GameSelectionModal({
  opened,
  onClose,
  games,
  selectedAppIds,
  onSave,
  isLoading = false
}: GameSelectionModalProps) {
  const [search, setSearch] = useState('');
  const [localSelected, setLocalSelected] = useState<Set<number>>(new Set());
  const [isSaving, setIsSaving] = useState(false);
  
  // Import state
  const [showImport, setShowImport] = useState(false);
  const [importText, setImportText] = useState('');
  const [importResult, setImportResult] = useState<{
    added: number;
    alreadySelected: number;
    notInLibrary: number[];
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
  const parseImportText = useCallback((text: string): number[] => {
    const trimmed = text.trim();
    if (!trimmed) return [];
    
    // Try JSON array first
    if (trimmed.startsWith('[')) {
      try {
        const parsed = JSON.parse(trimmed);
        if (Array.isArray(parsed)) {
          return parsed
            .map(id => typeof id === 'number' ? id : parseInt(String(id), 10))
            .filter(id => !isNaN(id) && id > 0);
        }
      } catch {
        // Fall through to other parsing methods
      }
    }
    
    // Split by comma, newline, or space
    return trimmed
      .split(/[,\n\s]+/)
      .map(s => parseInt(s.trim(), 10))
      .filter(id => !isNaN(id) && id > 0);
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
    const notInLibrary: number[] = [];

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

  // Filter games by search
  const filteredGames = useMemo(() => {
    if (!search.trim()) return games;
    const searchLower = search.toLowerCase();
    return games.filter(game =>
      game.name.toLowerCase().includes(searchLower) ||
      game.appId.toString().includes(search)
    );
  }, [games, search]);

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

  const toggleGame = useCallback((appId: number) => {
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

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title="Select Games to Prefill"
      size="lg"
    >
      <div className="flex flex-col" style={{ height: '60vh' }}>
        {/* Import Section - Collapsible */}
        <div className="mb-3">
          <button
            onClick={() => setShowImport(!showImport)}
            className="flex items-center gap-2 text-sm font-medium smooth-transition"
            style={{ color: 'var(--theme-primary)' }}
          >
            <Import className="h-4 w-4" />
            Import App IDs
            <ChevronDown 
              className={`h-4 w-4 smooth-transition ${showImport ? 'rotate-180' : ''}`} 
            />
          </button>
          
          {showImport && (
            <div
              className="mt-2 p-3 rounded-lg"
              style={{
                backgroundColor: 'var(--theme-bg-tertiary)',
                border: '1px solid var(--theme-border-secondary)'
              }}
            >
              <p className="text-xs mb-2" style={{ color: 'var(--theme-text-muted)' }}>
                Paste Steam App IDs (comma-separated, JSON array, or one per line)
              </p>
              <textarea
                value={importText}
                onChange={(e) => {
                  setImportText(e.target.value);
                  setImportResult(null);
                }}
                placeholder="730, 570, 440 or [730, 570, 440]"
                className="w-full px-3 py-2 text-sm rounded-lg resize-none smooth-transition"
                style={{
                  backgroundColor: 'var(--theme-bg-secondary)',
                  border: '1px solid var(--theme-border-secondary)',
                  color: 'var(--theme-text-primary)',
                  outline: 'none',
                  minHeight: '60px'
                }}
                onFocus={(e) => {
                  e.currentTarget.style.borderColor = 'var(--theme-primary)';
                }}
                onBlur={(e) => {
                  e.currentTarget.style.borderColor = 'var(--theme-border-secondary)';
                }}
              />
              <div className="flex items-center gap-2 mt-2">
                <Button 
                  variant="filled" 
                  size="sm" 
                  onClick={handleImport}
                  disabled={!importText.trim()}
                >
                  <Import className="h-3.5 w-3.5" />
                  Import
                </Button>
                {importResult && (
                  <span className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                    {importResult.added > 0 && (
                      <span style={{ color: 'var(--theme-success)' }}>
                        +{importResult.added} added
                      </span>
                    )}
                    {importResult.alreadySelected > 0 && (
                      <span>
                        {importResult.added > 0 ? ', ' : ''}
                        {importResult.alreadySelected} already selected
                      </span>
                    )}
                    {importResult.notInLibrary.length > 0 && (
                      <span style={{ color: 'var(--theme-warning)' }}>
                        {(importResult.added > 0 || importResult.alreadySelected > 0) ? ', ' : ''}
                        {importResult.notInLibrary.length} not in library
                      </span>
                    )}
                    {importResult.added === 0 && importResult.alreadySelected === 0 && importResult.notInLibrary.length === 0 && (
                      <span style={{ color: 'var(--theme-error)' }}>No valid App IDs found</span>
                    )}
                  </span>
                )}
              </div>
            </div>
          )}
        </div>

        {/* Search and actions */}
        <div className="flex gap-2 mb-3">
          <div className="relative flex-1">
            <Search
              className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4"
              style={{ color: 'var(--theme-text-muted)' }}
            />
            <input
              type="text"
              placeholder="Search games..."
              value={search}
              onChange={(e: ChangeEvent<HTMLInputElement>) => setSearch(e.target.value)}
              className="w-full pl-9 pr-3 py-2 text-sm rounded-lg smooth-transition"
              style={{
                backgroundColor: 'var(--theme-bg-tertiary)',
                border: '1px solid var(--theme-border-secondary)',
                color: 'var(--theme-text-primary)',
                outline: 'none'
              }}
              onFocus={(e) => {
                e.currentTarget.style.borderColor = 'var(--theme-primary)';
                e.currentTarget.style.boxShadow = '0 0 0 2px color-mix(in srgb, var(--theme-primary) 20%, transparent)';
              }}
              onBlur={(e) => {
                e.currentTarget.style.borderColor = 'var(--theme-border-secondary)';
                e.currentTarget.style.boxShadow = 'none';
              }}
            />
          </div>
          <Button variant="outline" size="sm" onClick={selectAll}>
            Select All
          </Button>
          <Button variant="outline" size="sm" onClick={selectNone}>
            Clear
          </Button>
        </div>

        {/* Selection count */}
        <div className="text-sm mb-2" style={{ color: 'var(--theme-text-muted)' }}>
          <span style={{ color: 'var(--theme-primary)', fontWeight: 600 }}>{localSelected.size}</span>
          {' '}of {games.length} games selected
          {search && (
            <span style={{ color: 'var(--theme-text-muted)' }}>
              {' '}(showing {filteredGames.length} matching "{search}")
            </span>
          )}
        </div>

        {/* Game list */}
        <div
          className="flex-1 relative rounded-lg overflow-hidden min-h-0"
          style={{
            backgroundColor: 'var(--theme-bg-tertiary)',
            border: '1px solid var(--theme-border-secondary)'
          }}
        >
          {isLoading ? (
            <div className="flex items-center justify-center h-full">
              <Loader2 className="h-8 w-8 animate-spin" style={{ color: 'var(--theme-primary)' }} />
            </div>
          ) : sortedGames.length === 0 ? (
            <div
              className="flex flex-col items-center justify-center h-full"
              style={{ color: 'var(--theme-text-muted)' }}
            >
              <div
                className="w-16 h-16 rounded-xl flex items-center justify-center mb-3"
                style={{ backgroundColor: 'var(--theme-bg-secondary)' }}
              >
                <Gamepad2 className="h-8 w-8 opacity-50" />
              </div>
              <p className="font-medium">No games found</p>
              {search && (
                <p className="text-sm mt-1 opacity-70">Try a different search term</p>
              )}
            </div>
          ) : (
            <div className="absolute inset-0 flex flex-col overflow-hidden rounded-lg">
              {/* Selected games section - sticky at top */}
              {sortedGames.some(g => localSelected.has(g.appId)) && (
                <div
                  className="flex-shrink-0"
                  style={{
                    maxHeight: '40%',
                    display: 'flex',
                    flexDirection: 'column'
                  }}
                >
                  <div
                    className="px-4 py-2 text-xs font-semibold uppercase tracking-wider flex-shrink-0"
                    style={{
                      backgroundColor: 'color-mix(in srgb, var(--theme-primary) 15%, var(--theme-bg-tertiary))',
                      color: 'var(--theme-primary)',
                      borderBottom: '1px solid var(--theme-border-secondary)'
                    }}
                  >
                    Selected ({localSelected.size})
                  </div>
                  <CustomScrollbar maxHeight="100%" className="flex-1 min-h-0" paddingMode="compact">
                    <div>
                      {sortedGames.filter(g => localSelected.has(g.appId)).map(game => (
                        <button
                          key={game.appId}
                          onClick={() => toggleGame(game.appId)}
                          className="w-full flex items-center gap-3 px-4 py-3 text-left smooth-transition"
                          style={{
                            backgroundColor: 'color-mix(in srgb, var(--theme-primary) 10%, transparent)',
                            borderBottom: '1px solid var(--theme-border-secondary)'
                          }}
                          onMouseEnter={(e) => {
                            e.currentTarget.style.backgroundColor = 'color-mix(in srgb, var(--theme-primary) 15%, transparent)';
                          }}
                          onMouseLeave={(e) => {
                            e.currentTarget.style.backgroundColor = 'color-mix(in srgb, var(--theme-primary) 10%, transparent)';
                          }}
                        >
                          <div
                            className="flex-shrink-0 w-5 h-5 rounded flex items-center justify-center"
                            style={{
                              backgroundColor: 'var(--theme-primary)',
                              border: '2px solid var(--theme-primary)'
                            }}
                          >
                            <Check className="h-3 w-3" style={{ color: 'white' }} />
                          </div>
                          <div className="flex-1 min-w-0">
                            <div className="truncate font-medium" style={{ color: 'var(--theme-text-primary)' }}>
                              {game.name}
                            </div>
                            <div className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                              App ID: {game.appId}
                            </div>
                          </div>
                        </button>
                      ))}
                    </div>
                  </CustomScrollbar>
                </div>
              )}

              {/* Available games section */}
              {sortedGames.some(g => !localSelected.has(g.appId)) && (
                <div className="flex-1 min-h-0 flex flex-col">
                  <div
                    className="px-4 py-2 text-xs font-semibold uppercase tracking-wider flex-shrink-0"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      color: 'var(--theme-text-muted)',
                      borderBottom: '1px solid var(--theme-border-secondary)'
                    }}
                  >
                    Available Games ({sortedGames.filter(g => !localSelected.has(g.appId)).length})
                  </div>
                  <CustomScrollbar maxHeight="100%" className="flex-1 min-h-0" paddingMode="compact">
                    <div>
                      {sortedGames.filter(g => !localSelected.has(g.appId)).map(game => (
                        <button
                          key={game.appId}
                          onClick={() => toggleGame(game.appId)}
                          className="w-full flex items-center gap-3 px-4 py-3 text-left smooth-transition"
                          style={{
                            backgroundColor: 'transparent',
                            borderBottom: '1px solid var(--theme-border-secondary)'
                          }}
                          onMouseEnter={(e) => {
                            e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                          }}
                          onMouseLeave={(e) => {
                            e.currentTarget.style.backgroundColor = 'transparent';
                          }}
                        >
                          <div
                            className="flex-shrink-0 w-5 h-5 rounded flex items-center justify-center"
                            style={{
                              backgroundColor: 'transparent',
                              border: '2px solid var(--theme-border-primary)'
                            }}
                          />
                          <div className="flex-1 min-w-0">
                            <div className="truncate font-medium" style={{ color: 'var(--theme-text-primary)' }}>
                              {game.name}
                            </div>
                            <div className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                              App ID: {game.appId}
                            </div>
                          </div>
                        </button>
                      ))}
                    </div>
                  </CustomScrollbar>
                </div>
              )}
            </div>
          )}
        </div>

        {/* Actions */}
        <div
          className="flex justify-end gap-2 mt-4 pt-4"
          style={{ borderTop: '1px solid var(--theme-border-secondary)' }}
        >
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="filled" onClick={handleSave} disabled={isSaving}>
            {isSaving ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <Check className="h-4 w-4" />
            )}
            Save Selection ({localSelected.size})
          </Button>
        </div>
      </div>
    </Modal>
  );
}
