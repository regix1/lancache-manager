import React, { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Search } from 'lucide-react';
import { Pagination } from '@components/ui/Pagination';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import Badge from '@components/ui/Badge';
import { usePaginatedList } from '@hooks/usePaginatedList';
import type { CorruptedChunkDetail } from '@/types';
import '../managementSectionContent.css';

interface CorruptionChunkListProps {
  chunks: CorruptedChunkDetail[];
  /** Which findings to render. Removable and review-only findings live in separate,
   *  top-level lists, so each mount shows exactly one kind. */
  variant: 'removable' | 'review';
}

// A single service can report thousands of corrupted chunks; cap what is mounted per
// page so the expanded row never renders a giant list, and give the search box/pagination
// something to do only once the list is large enough to warrant them.
const CHUNKS_PER_PAGE = 25;

/**
 * Renders one service's corrupted-chunk details of a single kind (removable OR review-only,
 * chosen by the parent via `variant`) with a search box, pagination and the house
 * CustomScrollbar. Search filters by candidate identity, request URL, physical slice and
 * exact on-disk paths.
 */
const CorruptionChunkList: React.FC<CorruptionChunkListProps> = ({ chunks, variant }) => {
  const { t } = useTranslation();
  const [searchQuery, setSearchQuery] = useState('');

  // Every service fetch returns all of its chunks; show only the kind this list owns.
  const items = useMemo(
    () =>
      chunks.filter((chunk) =>
        variant === 'removable' ? chunk.removal_allowed : !chunk.removal_allowed
      ),
    [chunks, variant]
  );

  // Search + pagination only earn their space once the list exceeds a page; smaller
  // lists render straight into the scrollbar so tiny services stay uncluttered.
  const enableControls = items.length > CHUNKS_PER_PAGE;

  const filteredChunks = useMemo(() => {
    const query = searchQuery.trim().toLowerCase();
    if (!query) return items;
    return items.filter((chunk) => {
      const searchable = [
        chunk.candidate_id,
        chunk.datasource,
        chunk.raw_url,
        chunk.normalized_uri,
        chunk.retry_client ?? '',
        chunk.reason,
        chunk.validation_state,
        chunk.supporting_sibling?.exact_path ?? '',
        ...chunk.exact_paths
      ]
        .join(' ')
        .toLowerCase();
      return searchable.includes(query);
    });
  }, [items, searchQuery]);

  const { page, setPage, totalPages, paginatedItems } = usePaginatedList<CorruptedChunkDetail>({
    items: filteredChunks,
    pageSize: CHUNKS_PER_PAGE,
    resetKey: searchQuery
  });

  const visibleChunks = filteredChunks.length > CHUNKS_PER_PAGE ? paginatedItems : filteredChunks;

  const renderChunk = (chunk: CorruptedChunkDetail) => {
    const observedRange =
      chunk.observed_range.kind === 'inclusive'
        ? `bytes=${chunk.observed_range.start}-${chunk.observed_range.end}`
        : t('management.corruption.noRange');
    const physicalSlice =
      chunk.cache_slice.kind === 'ranged'
        ? `bytes=${chunk.cache_slice.start}-${chunk.cache_slice.end}`
        : t(`management.corruption.sliceKinds.${chunk.cache_slice.kind}`, {
            defaultValue: chunk.cache_slice.kind
          });
    const supportingSiblingSlice = chunk.supporting_sibling
      ? chunk.supporting_sibling.cache_slice.kind === 'ranged'
        ? `bytes=${chunk.supporting_sibling.cache_slice.start}-${chunk.supporting_sibling.cache_slice.end}`
        : t(`management.corruption.sliceKinds.${chunk.supporting_sibling.cache_slice.kind}`, {
            defaultValue: chunk.supporting_sibling.cache_slice.kind
          })
      : null;
    const reason = t(`management.corruption.reasons.${chunk.reason}`, {
      defaultValue: chunk.reason
    });
    const validation = t(`management.corruption.validationStates.${chunk.validation_state}`, {
      defaultValue: chunk.validation_state
    });

    return (
      <div key={chunk.candidate_id} className="mgmt-evidence">
        <div className="mgmt-evidence__head">
          <div className="mgmt-evidence__ident">
            <code className="mgmt-evidence__exact-value text-themed-primary">{chunk.raw_url}</code>
            {chunk.normalized_uri !== chunk.raw_url && (
              <span className="mgmt-evidence__normalized">
                <span>{t('management.corruption.normalizedUri')}</span>
                <code className="mgmt-evidence__exact-value">{chunk.normalized_uri}</code>
              </span>
            )}
          </div>
          <span className="mgmt-evidence__count">
            {t('management.corruption.evidenceCount')}{' '}
            <strong className="text-themed-error">{chunk.evidence_count}</strong>
          </span>
        </div>

        <div className="mgmt-evidence__tags">
          <Badge variant={chunk.validation_state === 'exact_path_present' ? 'success' : 'info'}>
            {validation}
          </Badge>
        </div>

        <dl className="mgmt-kv">
          <div className="mgmt-kv__cell">
            <dt>{t('management.corruption.datasource')}</dt>
            <dd>{chunk.datasource}</dd>
          </div>
          <div className="mgmt-kv__cell">
            <dt>{t('management.corruption.observedRange')}</dt>
            <dd>
              <code>{observedRange}</code>
            </dd>
          </div>
          <div className="mgmt-kv__cell">
            <dt>{t('management.corruption.physicalSlice')}</dt>
            <dd>
              <code>{physicalSlice}</code>
            </dd>
          </div>
          <div className="mgmt-kv__cell">
            <dt>{t('management.corruption.reason')}</dt>
            <dd>{reason}</dd>
          </div>
          <div className="mgmt-kv__cell mgmt-kv__cell--wide">
            <dt>
              {t(
                chunk.reason === 'missing_cached_slice'
                  ? 'management.corruption.expectedMissingPath'
                  : 'management.corruption.cache'
              )}
            </dt>
            <dd>
              {chunk.exact_paths.length > 0 ? (
                chunk.exact_paths.map((path) => (
                  <code key={path} className="mgmt-evidence__exact-value">
                    {path}
                  </code>
                ))
              ) : (
                <span className="block">{t('management.corruption.noExactPath')}</span>
              )}
            </dd>
          </div>
          {chunk.supporting_sibling && (
            <div className="mgmt-kv__cell mgmt-kv__cell--wide">
              <dt>{t('management.corruption.supportingSibling')}</dt>
              <dd>
                {supportingSiblingSlice && <code>{supportingSiblingSlice}</code>}
                <code className="mgmt-evidence__exact-value">
                  {chunk.supporting_sibling.exact_path}
                </code>
              </dd>
            </div>
          )}
          {chunk.retry_client && (
            <div className="mgmt-kv__cell mgmt-kv__cell--wide">
              <dt>{t('management.corruption.retryEvidenceLabel')}</dt>
              <dd>
                {t('management.corruption.retryEvidence', {
                  client: chunk.retry_client,
                  first: chunk.first_seen,
                  last: chunk.last_seen
                })}
              </dd>
            </div>
          )}
        </dl>
      </div>
    );
  };

  if (items.length === 0) {
    return (
      <p className="py-4 text-center text-sm text-themed-muted">
        {t('management.corruption.noDetailsAvailable')}
      </p>
    );
  }

  return (
    <div className="space-y-3">
      {enableControls && (
        <div className="relative">
          <Search className="input-icon absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
          <input
            type="text"
            placeholder={t('management.corruption.searchPlaceholder')}
            aria-label={t('management.corruption.searchPlaceholder')}
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="themed-input w-full pl-10 pr-12 py-2 text-sm"
          />
          {searchQuery && (
            <button
              type="button"
              onClick={() => setSearchQuery('')}
              aria-label={t('common.clear')}
              className="absolute right-3 top-1/2 transform -translate-y-1/2 text-themed-muted hover:text-themed-primary text-xs"
            >
              {t('common.clear')}
            </button>
          )}
        </div>
      )}

      {filteredChunks.length === 0 ? (
        <p className="py-6 text-center text-sm text-themed-muted">
          {t('management.corruption.noSearchMatch')}
        </p>
      ) : (
        <>
          <CustomScrollbar maxHeight="24rem" radius="none" paddingMode="compact">
            <div className="mgmt-evidence-list">{visibleChunks.map(renderChunk)}</div>
          </CustomScrollbar>
          {filteredChunks.length > CHUNKS_PER_PAGE && (
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
        </>
      )}
    </div>
  );
};

export default CorruptionChunkList;
