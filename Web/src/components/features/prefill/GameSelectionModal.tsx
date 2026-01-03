import { useState, useMemo, useCallback, useEffect, useRef, ChangeEvent } from 'react';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { CustomScrollbar } from '../../ui/CustomScrollbar';
import { Search, Check, Gamepad2, Loader2 } from 'lucide-react';

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
  const [listHeight, setListHeight] = useState(400);
  const listContainerRef = useRef<HTMLDivElement>(null);

  // Reset local selection when modal opens - start fresh each time
  useEffect(() => {
    if (opened) {
      // Start with current selection from parent, not cached
      setLocalSelected(new Set(selectedAppIds));
      setSearch('');
    }
  }, [opened, selectedAppIds]);

  // Measure list container height for CustomScrollbar
  useEffect(() => {
    if (!opened || !listContainerRef.current) return;

    const updateHeight = () => {
      if (listContainerRef.current) {
        setListHeight(listContainerRef.current.clientHeight);
      }
    };

    // Initial measurement with delay for modal animation
    const timer = setTimeout(updateHeight, 50);

    // Update on resize
    const resizeObserver = new ResizeObserver(updateHeight);
    resizeObserver.observe(listContainerRef.current);

    return () => {
      clearTimeout(timer);
      resizeObserver.disconnect();
    };
  }, [opened]);

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
          ref={listContainerRef}
          className="flex-1 rounded-lg overflow-hidden min-h-0"
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
            <CustomScrollbar maxHeight={`${listHeight}px`} paddingMode="compact">
              <div>
                {sortedGames.map(game => {
                  const isSelected = localSelected.has(game.appId);
                  return (
                    <button
                      key={game.appId}
                      onClick={() => toggleGame(game.appId)}
                      className="w-full flex items-center gap-3 px-4 py-3 text-left smooth-transition"
                      style={{
                        backgroundColor: isSelected
                          ? 'color-mix(in srgb, var(--theme-primary) 10%, transparent)'
                          : 'transparent',
                        borderBottom: '1px solid var(--theme-border-secondary)'
                      }}
                      onMouseEnter={(e) => {
                        if (!isSelected) {
                          e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                        }
                      }}
                      onMouseLeave={(e) => {
                        e.currentTarget.style.backgroundColor = isSelected
                          ? 'color-mix(in srgb, var(--theme-primary) 10%, transparent)'
                          : 'transparent';
                      }}
                    >
                      {/* Checkbox */}
                      <div
                        className="flex-shrink-0 w-5 h-5 rounded flex items-center justify-center smooth-transition"
                        style={{
                          backgroundColor: isSelected
                            ? 'var(--theme-primary)'
                            : 'transparent',
                          border: isSelected
                            ? '2px solid var(--theme-primary)'
                            : '2px solid var(--theme-border-primary)'
                        }}
                      >
                        {isSelected && (
                          <Check className="h-3 w-3" style={{ color: 'white' }} />
                        )}
                      </div>

                      {/* Game info */}
                      <div className="flex-1 min-w-0">
                        <div
                          className="truncate font-medium"
                          style={{ color: 'var(--theme-text-primary)' }}
                        >
                          {game.name}
                        </div>
                        <div
                          className="text-xs"
                          style={{ color: 'var(--theme-text-muted)' }}
                        >
                          App ID: {game.appId}
                        </div>
                      </div>

                      {/* Selected indicator */}
                      {isSelected && (
                        <div
                          className="flex-shrink-0 px-2 py-0.5 rounded-full text-xs font-medium"
                          style={{
                            backgroundColor: 'color-mix(in srgb, var(--theme-primary) 15%, transparent)',
                            color: 'var(--theme-primary)'
                          }}
                        >
                          Selected
                        </div>
                      )}
                    </button>
                  );
                })}
              </div>
            </CustomScrollbar>
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
