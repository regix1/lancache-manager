import React, { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronUp, Search } from 'lucide-react';
import { Pagination } from '@components/ui/Pagination';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import Badge from '@components/ui/Badge';
import { usePaginatedList } from '@hooks/usePaginatedList';
import { formatCount } from '@utils/formatters';
import type { CorruptedChunkDetail } from '@/types';
import '../managementSectionContent.css';

interface CorruptionChunkListProps {
  chunks: CorruptedChunkDetail[];
}

// A single service can report thousands of corrupted chunks; cap what is mounted per
// page so the expanded row never renders a giant list, and give the search box/pagination
// something to do only once the list is large enough to warrant them.
const CHUNKS_PER_PAGE = 25;

/**
 * Renders one service's corrupted-chunk details with a search box, pagination and the
 * house CustomScrollbar. Findings are split into a "Removable" section (actionable) and a
 * "Review only" section that stays collapsed by default, since those findings can't be
 * removed and are mostly informational. Search filters by candidate identity, request URL,
 * physical slice and exact on-disk paths.
 */
const CorruptionChunkList: React.FC<CorruptionChunkListProps> = ({ chunks }) => {
  const { t } = useTranslation();
  const [searchQuery, setSearchQuery] = useState('');
  // Review-only findings are noise the user can't act on, so keep them tucked away unless
  // this service is review-only end to end (then there is nothing else to show).
  const [reviewOpen, setReviewOpen] = useState(
    () => chunks.length > 0 && chunks.every((chunk) => !chunk.removal_allowed)
  );

  // Search + pagination only earn their space once the list exceeds a page; smaller
  // lists render straight into the scrollbar so tiny services stay uncluttered.
  const enableControls = chunks.length > CHUNKS_PER_PAGE;

  const filteredChunks = useMemo(() => {
    const query = searchQuery.trim().toLowerCase();
    if (!query) return chunks;
    return chunks.filter((chunk) => {
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
  }, [chunks, searchQuery]);

  const removableChunks = useMemo(
    () => filteredChunks.filter((chunk) => chunk.removal_allowed),
    [filteredChunks]
  );
  const reviewChunks = useMemo(
    () => filteredChunks.filter((chunk) => !chunk.removal_allowed),
    [filteredChunks]
  );

  // Each section paginates independently so opening the review-only list never disturbs the
  // removable page, and a huge review list stays cheap while collapsed.
  const {
    page: removablePage,
    setPage: setRemovablePage,
    totalPages: removableTotalPages,
    paginatedItems: removableItems
  } = usePaginatedList<CorruptedChunkDetail>({
    items: removableChunks,
    pageSize: CHUNKS_PER_PAGE,
    resetKey: searchQuery
  });
  const {
    page: reviewPage,
    setPage: setReviewPage,
    totalPages: reviewTotalPages,
    paginatedItems: reviewItems
  } = usePaginatedList<CorruptedChunkDetail>({
    items: reviewChunks,
    pageSize: CHUNKS_PER_PAGE,
    resetKey: searchQuery
  });

  const removableVisible =
    removableChunks.length > CHUNKS_PER_PAGE ? removableItems : removableChunks;
  const reviewVisible = reviewChunks.length > CHUNKS_PER_PAGE ? reviewItems : reviewChunks;

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
          <Badge
            variant={
              chunk.validation_state === 'exact_path_present'
                ? 'success'
                : chunk.validation_state === 'exact_path_missing'
                  ? 'neutral'
                  : 'info'
            }
          >
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

  const hasResults = removableChunks.length > 0 || reviewChunks.length > 0;

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

      {!hasResults ? (
        <p className="py-6 text-center text-sm text-themed-muted">
          {t('management.corruption.noSearchMatch')}
        </p>
      ) : (
        <>
          {removableChunks.length > 0 && (
            <section className="mgmt-evidence-group mgmt-evidence-group--removable">
              <p className="mgmt-evidence-grouphead">
                <span className="text-themed-success">
                  {t('management.corruption.removableSectionTitle')}
                </span>
                <span className="mgmt-evidence-grouphead__count">
                  {formatCount(removableChunks.length)}
                </span>
              </p>
              <CustomScrollbar maxHeight="24rem" radius="none" paddingMode="compact">
                <div className="mgmt-evidence-list">{removableVisible.map(renderChunk)}</div>
              </CustomScrollbar>
              {removableChunks.length > CHUNKS_PER_PAGE && (
                <Pagination
                  currentPage={removablePage}
                  totalPages={removableTotalPages}
                  totalItems={removableChunks.length}
                  itemsPerPage={CHUNKS_PER_PAGE}
                  onPageChange={setRemovablePage}
                  itemLabel={t('management.corruption.chunkLabel')}
                  variant="compact"
                  showCard={false}
                />
              )}
            </section>
          )}

          {reviewChunks.length > 0 && (
            <section className="mgmt-evidence-group mgmt-evidence-group--review">
              <button
                type="button"
                className="mgmt-evidence-grouptoggle"
                onClick={() => setReviewOpen((open) => !open)}
                aria-expanded={reviewOpen}
              >
                {reviewOpen ? (
                  <ChevronUp className="mgmt-evidence-grouptoggle__icon w-3.5 h-3.5" />
                ) : (
                  <ChevronDown className="mgmt-evidence-grouptoggle__icon w-3.5 h-3.5" />
                )}
                <span>{t('management.corruption.reviewSectionTitle')}</span>
                <span className="mgmt-evidence-grouphead__count">
                  {formatCount(reviewChunks.length)}
                </span>
              </button>
              <CollapsibleRegion open={reviewOpen}>
                <p className="mgmt-evidence-groupnote">
                  {t('management.corruption.reviewSectionNote')}
                </p>
                <CustomScrollbar maxHeight="24rem" radius="none" paddingMode="compact">
                  <div className="mgmt-evidence-list">{reviewVisible.map(renderChunk)}</div>
                </CustomScrollbar>
                {reviewChunks.length > CHUNKS_PER_PAGE && (
                  <Pagination
                    currentPage={reviewPage}
                    totalPages={reviewTotalPages}
                    totalItems={reviewChunks.length}
                    itemsPerPage={CHUNKS_PER_PAGE}
                    onPageChange={setReviewPage}
                    itemLabel={t('management.corruption.chunkLabel')}
                    variant="compact"
                    showCard={false}
                  />
                )}
              </CollapsibleRegion>
            </section>
          )}
        </>
      )}
    </div>
  );
};

export default CorruptionChunkList;
