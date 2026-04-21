import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Search } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Pagination } from '@components/ui/Pagination';
import { usePaginatedList } from '@hooks/usePaginatedList';

interface CacheEntityListRenderState {
  itemId: string;
  isExpanded: boolean;
  isExpanding: boolean;
  onToggleDetails: (itemId: string) => void;
}

interface CacheEntityListProps<TItem> {
  items: TItem[];
  searchPlaceholder: string;
  getEmptyMessage: (query: string) => string;
  itemLabel: string;
  getItemKey: (item: TItem) => string;
  filterAndSortItems: (items: TItem[], query: string) => TItem[];
  renderItem: (item: TItem, state: CacheEntityListRenderState) => React.ReactNode;
}

const ITEMS_PER_PAGE = 20;
const PAGINATION_TOP_THRESHOLD = 100;
const EXPAND_SPINNER_DELAY_MS = 50;

function CacheEntityList<TItem>({
  items,
  searchPlaceholder,
  getEmptyMessage,
  itemLabel,
  getItemKey,
  filterAndSortItems,
  renderItem
}: CacheEntityListProps<TItem>) {
  const { t } = useTranslation();
  const [searchQuery, setSearchQuery] = useState('');
  const [expandedItemId, setExpandedItemId] = useState<string | null>(null);
  const [expandingItemId, setExpandingItemId] = useState<string | null>(null);
  const expandTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      if (expandTimeoutRef.current !== null) {
        clearTimeout(expandTimeoutRef.current);
      }
    },
    []
  );

  const filteredAndSortedItems = useMemo(
    () => filterAndSortItems(items, searchQuery),
    [items, searchQuery, filterAndSortItems]
  );

  const {
    page: currentPage,
    setPage: setCurrentPage,
    totalPages,
    paginatedItems
  } = usePaginatedList<TItem>({
    items: filteredAndSortedItems,
    pageSize: ITEMS_PER_PAGE,
    resetKey: searchQuery
  });

  const toggleItemDetails = useCallback(
    (itemId: string) => {
      if (expandTimeoutRef.current !== null) {
        clearTimeout(expandTimeoutRef.current);
        expandTimeoutRef.current = null;
      }

      if (expandedItemId === itemId) {
        setExpandedItemId(null);
        setExpandingItemId(null);
        return;
      }

      setExpandingItemId(itemId);
      expandTimeoutRef.current = setTimeout(() => {
        setExpandedItemId(itemId);
        setExpandingItemId(null);
        expandTimeoutRef.current = null;
      }, EXPAND_SPINNER_DELAY_MS);
    },
    [expandedItemId]
  );

  if (items.length === 0) {
    return null;
  }

  return (
    <div>
      <div className="mb-3">
        <div className="relative">
          <Search className="input-icon absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
          <input
            type="text"
            placeholder={searchPlaceholder}
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="w-full pl-10 pr-4 py-2 rounded-lg border text-sm bg-themed-secondary border-themed-secondary text-themed-primary"
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
      </div>

      {filteredAndSortedItems.length === 0 && (
        <div className="text-center py-8 text-themed-muted">
          <Search className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <div className="mb-2">{getEmptyMessage(searchQuery)}</div>
          <Button variant="subtle" size="sm" onClick={() => setSearchQuery('')}>
            {t('management.gameDetection.clearSearch')}
          </Button>
        </div>
      )}

      {filteredAndSortedItems.length > 0 && (
        <>
          {filteredAndSortedItems.length > PAGINATION_TOP_THRESHOLD && totalPages > 1 && (
            <Pagination
              currentPage={currentPage}
              totalPages={totalPages}
              totalItems={filteredAndSortedItems.length}
              itemsPerPage={ITEMS_PER_PAGE}
              onPageChange={setCurrentPage}
              itemLabel={itemLabel}
            />
          )}

          <div className="space-y-3">
            {paginatedItems.map((item) => {
              const itemId = getItemKey(item);
              return (
                <React.Fragment key={itemId}>
                  {renderItem(item, {
                    itemId,
                    isExpanded: expandedItemId === itemId,
                    isExpanding: expandingItemId === itemId,
                    onToggleDetails: toggleItemDetails
                  })}
                </React.Fragment>
              );
            })}
          </div>

          <Pagination
            currentPage={currentPage}
            totalPages={totalPages}
            totalItems={filteredAndSortedItems.length}
            itemsPerPage={ITEMS_PER_PAGE}
            onPageChange={setCurrentPage}
            itemLabel={itemLabel}
          />
        </>
      )}
    </div>
  );
}

export default CacheEntityList;
