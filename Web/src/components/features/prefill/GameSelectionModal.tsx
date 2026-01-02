import { useState, useMemo, useCallback, useEffect, ChangeEvent } from 'react';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { Search, Check, X, Gamepad2, Loader2 } from 'lucide-react';

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
  const [localSelected, setLocalSelected] = useState<Set<number>>(new Set(selectedAppIds));
  const [isSaving, setIsSaving] = useState(false);

  // Reset local selection when modal opens or selectedAppIds change
  useEffect(() => {
    if (opened) {
      setLocalSelected(new Set(selectedAppIds));
      setSearch('');
    }
  }, [opened, selectedAppIds]);

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
      <div className="flex flex-col h-[60vh]">
        {/* Search and actions */}
        <div className="flex gap-2 mb-3">
          <div className="relative flex-1">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
            <input
              type="text"
              placeholder="Search games..."
              value={search}
              onChange={(e: ChangeEvent<HTMLInputElement>) => setSearch(e.target.value)}
              className="w-full pl-9 pr-3 py-2 text-sm border rounded-md bg-background focus:outline-none focus:ring-2 focus:ring-ring"
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
        <div className="text-sm text-muted-foreground mb-2">
          {localSelected.size} of {games.length} games selected
          {search && ` (showing ${filteredGames.length} matching "${search}")`}
        </div>

        {/* Game list */}
        <div className="flex-1 overflow-y-auto border rounded-md">
          {isLoading ? (
            <div className="flex items-center justify-center h-full">
              <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
            </div>
          ) : sortedGames.length === 0 ? (
            <div className="flex flex-col items-center justify-center h-full text-muted-foreground">
              <Gamepad2 className="h-12 w-12 mb-2 opacity-50" />
              <p>No games found</p>
            </div>
          ) : (
            <div className="divide-y">
              {sortedGames.map(game => {
                const isSelected = localSelected.has(game.appId);
                return (
                  <button
                    key={game.appId}
                    onClick={() => toggleGame(game.appId)}
                    className={`w-full flex items-center gap-3 px-3 py-2 text-left hover:bg-accent/50 transition-colors ${
                      isSelected ? 'bg-accent/30' : ''
                    }`}
                  >
                    <div className={`flex-shrink-0 w-5 h-5 rounded border-2 flex items-center justify-center ${
                      isSelected
                        ? 'bg-primary border-primary text-primary-foreground'
                        : 'border-muted-foreground/30'
                    }`}>
                      {isSelected && <Check className="h-3 w-3" />}
                    </div>
                    <div className="flex-1 min-w-0">
                      <div className="truncate font-medium">{game.name}</div>
                      <div className="text-xs text-muted-foreground">App ID: {game.appId}</div>
                    </div>
                  </button>
                );
              })}
            </div>
          )}
        </div>

        {/* Actions */}
        <div className="flex justify-end gap-2 mt-4 pt-4 border-t">
          <Button variant="outline" onClick={onClose}>
            <X className="h-4 w-4 mr-2" />
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={isSaving}>
            {isSaving ? (
              <Loader2 className="h-4 w-4 mr-2 animate-spin" />
            ) : (
              <Check className="h-4 w-4 mr-2" />
            )}
            Save Selection ({localSelected.size})
          </Button>
        </div>
      </div>
    </Modal>
  );
}
