import React, { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Search } from 'lucide-react';
import { Pagination } from '@components/ui/Pagination';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { Tooltip } from '@components/ui/Tooltip';
import { usePaginatedList } from '@hooks/usePaginatedList';
import type { CorruptedChunkDetail } from '@/types';

interface CorruptionChunkListProps {
  chunks: CorruptedChunkDetail[];
  isRedownloadMode: boolean;
}

// A single service can report thousands of corrupted chunks; cap what is mounted per
// page so the expanded row never renders a giant list, and give the search box/pagination
// something to do only once the list is large enough to warrant them.
const CHUNKS_PER_PAGE = 25;

/**
 * Renders one service's corrupted-chunk details with a search box, pagination and the
 * house CustomScrollbar (replacing the native overflow scroller). Search filters by both
 * the request URL and the on-disk cache file path.
 */
const CorruptionChunkList: React.FC<CorruptionChunkListProps> = ({ chunks, isRedownloadMode }) => {
  const { t } = useTranslation();
  const [searchQuery, setSearchQuery] = useState('');

  // Search + pagination only earn their space once the list exceeds a page; smaller
  // lists render straight into the scrollbar so tiny services stay uncluttered.
  const enableControls = chunks.length > CHUNKS_PER_PAGE;

  const filteredChunks = useMemo(() => {
    const query = searchQuery.trim().toLowerCase();
    if (!query) return chunks;
    return chunks.filter(
      (chunk) =>
        chunk.url.toLowerCase().includes(query) ||
        (chunk.cache_file_path ?? '').toLowerCase().includes(query)
    );
  }, [chunks, searchQuery]);

  const { page, setPage, totalPages, paginatedItems } = usePaginatedList<CorruptedChunkDetail>({
    items: filteredChunks,
    pageSize: CHUNKS_PER_PAGE,
    resetKey: searchQuery
  });

  const visibleChunks = enableControls ? paginatedItems : filteredChunks;

  return (
    <div className="space-y-3">
      {enableControls && (
        <div className="relative">
          <Search className="input-icon absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
          <input
            type="text"
            placeholder={t('management.corruption.searchPlaceholder')}
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="themed-input w-full pl-10 pr-12 py-2 text-sm"
          />
          {searchQuery && (
            <button
              onClick={() => setSearchQuery('')}
              className="absolute right-3 top-1/2 transform -translate-y-1/2 text-themed-muted hover:text-themed-primary text-xs"
            >
              {t('common.clear')}
            </button>
          )}
        </div>
      )}

      {visibleChunks.length > 0 ? (
        <CustomScrollbar maxHeight="24rem" radius="none" paddingMode="compact">
          <div className="divide-y divide-[var(--theme-border-secondary)]">
            {visibleChunks.map((chunk, idx) => (
              <div
                key={idx}
                className="flex items-start justify-between gap-3 py-2 first:pt-0 last:pb-0"
              >
                <div className="flex-1 min-w-0">
                  <Tooltip content={chunk.url}>
                    <span className="block font-mono text-xs text-themed-primary truncate">
                      {chunk.url}
                    </span>
                  </Tooltip>
                  {chunk.cache_file_path && (
                    <Tooltip content={chunk.cache_file_path}>
                      <span className="block text-xs text-themed-muted truncate">
                        {t('management.corruption.cache')}{' '}
                        <code>
                          {chunk.cache_file_path.split('/').pop() ||
                            chunk.cache_file_path.split('\\').pop()}
                        </code>
                      </span>
                    </Tooltip>
                  )}
                </div>
                <span className="text-xs text-themed-muted flex-shrink-0">
                  {isRedownloadMode
                    ? t('management.corruption.redownloadCount')
                    : t('management.corruption.missCount')}{' '}
                  <strong className="text-themed-error">{chunk.miss_count || 0}</strong>
                </span>
              </div>
            ))}
          </div>
        </CustomScrollbar>
      ) : (
        <p className="py-6 text-center text-sm text-themed-muted">
          {t('management.corruption.noSearchMatch')}
        </p>
      )}

      {enableControls && (
        <Pagination
          currentPage={page}
          totalPages={totalPages}
          totalItems={filteredChunks.length}
          itemsPerPage={CHUNKS_PER_PAGE}
          onPageChange={setPage}
          itemLabel={t('management.corruption.chunkLabel')}
          variant="compact"
          showCard={false}
        />
      )}
    </div>
  );
};

export default CorruptionChunkList;
