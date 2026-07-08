import { useCallback, useMemo, useState } from 'react';

/**
 * Client-only multi-select state for batch "Remove Selected" flows.
 *
 * Selection is tracked entirely in React state - toggling a checkbox issues NO
 * network request (see Checkbox.tsx for the rationale). Callers derive validity
 * from their current item list and prune stale keys via `setMany`/`clear` on
 * reload, so a key that disappears from the list never survives a refresh.
 */
export interface SelectionSet<K extends string> {
  /** The currently selected keys (read-only view). */
  selected: ReadonlySet<K>;
  /** Number of selected keys. */
  count: number;
  /** Whether a given key is selected. */
  isSelected: (key: K) => boolean;
  /** Toggle a single key on/off. */
  toggle: (key: K) => void;
  /** Select or deselect many keys at once (select-all / clear-visible). */
  setMany: (keys: K[], selected: boolean) => void;
  /** Clear the entire selection. */
  clear: () => void;
  /** Whether every key in `keys` is selected (drives the select-all checkbox). */
  allSelected: (keys: K[]) => boolean;
}

/**
 * The subset of a selection surface passed down to the list/card components that
 * render multi-select checkboxes. `isSelected`/`onToggle` drive per-row checkboxes;
 * the optional `allSelected`/`setMany` pair enables a scoped "select all" toggle.
 * Owning sections adapt their `SelectionSet` (or a prefixed view of it) into this shape.
 */
export interface SelectionAdapter {
  isSelected: (key: string) => boolean;
  onToggle: (key: string) => void;
  allSelected?: (keys: string[]) => boolean;
  setMany?: (keys: string[], selected: boolean) => void;
}

export function useSelectionSet<K extends string>(): SelectionSet<K> {
  const [selected, setSelected] = useState<ReadonlySet<K>>(() => new Set<K>());

  const isSelected = useCallback((key: K): boolean => selected.has(key), [selected]);

  const toggle = useCallback((key: K): void => {
    setSelected((prev) => {
      const next = new Set<K>(prev);
      if (next.has(key)) {
        next.delete(key);
      } else {
        next.add(key);
      }
      return next;
    });
  }, []);

  const setMany = useCallback((keys: K[], select: boolean): void => {
    setSelected((prev) => {
      const next = new Set<K>(prev);
      if (select) {
        for (const key of keys) {
          next.add(key);
        }
      } else {
        for (const key of keys) {
          next.delete(key);
        }
      }
      return next;
    });
  }, []);

  const clear = useCallback((): void => {
    setSelected((prev) => (prev.size === 0 ? prev : new Set<K>()));
  }, []);

  const allSelected = useCallback(
    (keys: K[]): boolean => keys.length > 0 && keys.every((key) => selected.has(key)),
    [selected]
  );

  return useMemo(
    () => ({
      selected,
      count: selected.size,
      isSelected,
      toggle,
      setMany,
      clear,
      allSelected
    }),
    [selected, isSelected, toggle, setMany, clear, allSelected]
  );
}
