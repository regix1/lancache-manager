import {
  useCallback,
  useMemo,
  type Dispatch,
  type SetStateAction,
  type PointerEvent as ReactPointerEvent
} from 'react';
import type { Download } from '../types';

interface UseGroupPaginationOptions {
  /** The filtered downloads to paginate by IP group */
  filteredDownloads: Download[];
  /** Number of IP groups to show per page */
  sessionsPerPage: number;
  /** Maximum number of items to show within each IP group */
  itemsPerSession: number;
  /** The group ID used as the key in the pages map */
  groupId: string;
  /** Shared pages state from the parent component */
  groupPages: Record<string, number>;
  /** Setter for the shared pages state */
  setGroupPages: Dispatch<SetStateAction<Record<string, number>>>;
  /** Start hold timer for rapid page change on long-press */
  startHoldTimer: (callback: () => void) => void;
  /** Stop hold timer */
  stopHoldTimer: () => void;
}

interface UseGroupPaginationReturn {
  /** Current page number (1-based, clamped to valid range) */
  currentPage: number;
  /** Total number of pages */
  totalPages: number;
  /** Paginated and limited IP groups: { [ip]: Download[] } */
  ipGroups: Record<string, Download[]>;
  /** All IP entries before pagination (for total count) */
  allIpEntries: [string, Download[]][];
  /** Change to a specific page */
  handlePageChange: (newPage: number) => void;
  /** Pointer down handler for hold-to-repeat page navigation */
  handlePointerHoldStart: (
    event: ReactPointerEvent<HTMLButtonElement>,
    direction: 'prev' | 'next'
  ) => void;
  /** Pointer up / cancel handler to stop hold-to-repeat */
  handlePointerHoldEnd: (event: ReactPointerEvent<HTMLButtonElement>) => void;
}

/**
 * Extracts the shared group pagination logic used by NormalView (GroupCard,
 * GridCardDrawerContent) and CompactView (GroupRow).
 *
 * Given a list of filtered downloads it:
 *  1. Groups them by client IP
 *  2. Paginates the IP groups
 *  3. Limits items within each IP group
 *  4. Provides page-change handlers including long-press rapid navigation
 */
export function useGroupPagination({
  filteredDownloads,
  sessionsPerPage,
  itemsPerSession,
  groupId,
  groupPages,
  setGroupPages,
  startHoldTimer,
  stopHoldTimer
}: UseGroupPaginationOptions): UseGroupPaginationReturn {
  // Group ALL filtered downloads by IP
  const allIpGroups = useMemo<Record<string, Download[]>>(
    () =>
      filteredDownloads.reduce(
        (acc: Record<string, Download[]>, d: Download) => {
          if (!acc[d.clientIp]) acc[d.clientIp] = [];
          acc[d.clientIp].push(d);
          return acc;
        },
        {} as Record<string, Download[]>
      ),
    [filteredDownloads]
  );

  const allIpEntries = useMemo<[string, Download[]][]>(
    () => Object.entries(allIpGroups),
    [allIpGroups]
  );

  // Paginate IP groups
  const rawCurrentPage = groupPages[groupId] || 1;
  const totalPages = Math.max(1, Math.ceil(allIpEntries.length / sessionsPerPage));
  const currentPage = Math.min(rawCurrentPage, Math.max(1, totalPages));

  const paginatedIpEntries = useMemo<[string, Download[]][]>(() => {
    const startIndex = (currentPage - 1) * sessionsPerPage;
    const endIndex = startIndex + sessionsPerPage;
    return allIpEntries.slice(startIndex, endIndex);
  }, [allIpEntries, currentPage, sessionsPerPage]);

  // Limit items within each IP group
  const ipGroups = useMemo<Record<string, Download[]>>(
    () =>
      Object.fromEntries(
        paginatedIpEntries.map(([ip, downloads]: [string, Download[]]) => [
          ip,
          downloads.slice(0, itemsPerSession)
        ])
      ) as Record<string, Download[]>,
    [paginatedIpEntries, itemsPerSession]
  );

  const handlePageChange = useCallback(
    (newPage: number): void => {
      setGroupPages((prev: Record<string, number>) => ({ ...prev, [groupId]: newPage }));
    },
    [setGroupPages, groupId]
  );

  const handlePointerHoldStart = useCallback(
    (event: ReactPointerEvent<HTMLButtonElement>, direction: 'prev' | 'next'): void => {
      const isPrevious = direction === 'prev';
      if ((isPrevious && currentPage === 1) || (!isPrevious && currentPage === totalPages)) {
        return;
      }

      event.currentTarget.setPointerCapture?.(event.pointerId);
      startHoldTimer(() => {
        setGroupPages((prev: Record<string, number>) => {
          const current = prev[groupId] || 1;
          const nextPage = isPrevious
            ? Math.max(1, current - 1)
            : Math.min(totalPages, current + 1);
          if (nextPage === current) {
            return prev;
          }
          return { ...prev, [groupId]: nextPage };
        });
      });
    },
    [currentPage, totalPages, startHoldTimer, setGroupPages, groupId]
  );

  const handlePointerHoldEnd = useCallback(
    (event: ReactPointerEvent<HTMLButtonElement>): void => {
      event.currentTarget.releasePointerCapture?.(event.pointerId);
      stopHoldTimer();
    },
    [stopHoldTimer]
  );

  return {
    currentPage,
    totalPages,
    ipGroups,
    allIpEntries,
    handlePageChange,
    handlePointerHoldStart,
    handlePointerHoldEnd
  };
}
