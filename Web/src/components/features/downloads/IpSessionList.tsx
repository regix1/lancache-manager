import React from 'react';
import { useTranslation } from 'react-i18next';
import { usePaginatedList } from '@hooks/usePaginatedList';
import { Pagination } from '@components/ui/Pagination';
import type { Download } from '../../../types';

interface IpSessionListProps {
  /** The IP this list is associated with — used as part of the reset key. */
  ip: string;
  /** All downloads belonging to the IP (NOT pre-paginated). */
  items: Download[];
  /** Page size — driven by the "Items/IP" dropdown in `SessionFilterBar`. */
  itemsPerPage: number;
  /** Renders a single session row. Called once per paginated item. */
  renderItem: (item: Download, index: number) => React.ReactNode;
  /** Wrapper className for the list container (e.g. `divide-y ...`). */
  className?: string;
}

/**
 * Paginates the downloads inside a single IP group.
 *
 * Each IP gets its own component instance so it can hold its own pagination
 * state without violating the Rules of Hooks. When the total number of items
 * exceeds `itemsPerPage`, an inline `<Pagination>` control is rendered below
 * the items. Otherwise no pagination chrome is shown.
 */
const IpSessionList: React.FC<IpSessionListProps> = ({
  ip,
  items,
  itemsPerPage,
  renderItem,
  className
}) => {
  const { t } = useTranslation();
  const { page, setPage, totalPages, paginatedItems } = usePaginatedList<Download>({
    items,
    pageSize: itemsPerPage,
    resetKey: ip
  });

  return (
    <>
      <div className={className}>
        {paginatedItems.map((item, index) => renderItem(item, index))}
      </div>
      {totalPages > 1 && (
        <Pagination
          variant="inline"
          showCard={false}
          currentPage={page}
          totalPages={totalPages}
          onPageChange={setPage}
          holdToRepeat
          previousLabel={t('ui.pagination.previousPage')}
          nextLabel={t('ui.pagination.nextPage')}
          className="ip-session-pagination"
        />
      )}
    </>
  );
};

export default IpSessionList;
