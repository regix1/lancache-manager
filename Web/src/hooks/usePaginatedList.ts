import { useCallback, useEffect, useMemo, useState } from 'react';

interface UsePaginatedListOptions<T> {
  items: T[];
  pageSize: number;
  page?: number; // controlled mode if provided
  onPageChange?: (page: number) => void;
  initialPage?: number; // uncontrolled default (1 if omitted)
  resetKey?: string | number | boolean;
}

interface UsePaginatedListResult<T> {
  page: number;
  setPage: (page: number) => void;
  pageSize: number;
  totalPages: number; // always >= 1
  totalItems: number;
  paginatedItems: T[];
}

export function usePaginatedList<T>(
  options: UsePaginatedListOptions<T>
): UsePaginatedListResult<T> {
  const {
    items,
    pageSize,
    page: controlledPage,
    onPageChange,
    initialPage = 1,
    resetKey
  } = options;
  const isControlled = controlledPage !== undefined;
  const [internalPage, setInternalPage] = useState<number>(initialPage);
  const rawPage = isControlled ? (controlledPage as number) : internalPage;

  const totalItems = items.length;
  const totalPages = Math.max(1, Math.ceil(totalItems / Math.max(1, pageSize)));
  const clampedPage = Math.min(Math.max(1, rawPage), totalPages);

  // If controlled and out of range, notify parent. Skip when totalPages just became 0/1 from empty list to avoid infinite loops.
  useEffect(() => {
    if (isControlled && rawPage !== clampedPage && onPageChange) {
      onPageChange(clampedPage);
    } else if (!isControlled && rawPage !== clampedPage) {
      setInternalPage(clampedPage);
    }
  }, [isControlled, rawPage, clampedPage, onPageChange]);

  // Reset to page 1 when resetKey changes
  useEffect(() => {
    if (resetKey === undefined) return;
    if (isControlled) {
      onPageChange?.(1);
    } else {
      setInternalPage(1);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetKey]);

  const setPage = useCallback(
    (next: number) => {
      const safe = Math.min(Math.max(1, next), totalPages);
      if (isControlled) {
        onPageChange?.(safe);
      } else {
        setInternalPage(safe);
      }
    },
    [isControlled, onPageChange, totalPages]
  );

  const paginatedItems = useMemo<T[]>(() => {
    const start = (clampedPage - 1) * pageSize;
    return items.slice(start, start + pageSize);
  }, [items, clampedPage, pageSize]);

  return {
    page: clampedPage,
    setPage,
    pageSize,
    totalPages,
    totalItems,
    paginatedItems
  };
}
