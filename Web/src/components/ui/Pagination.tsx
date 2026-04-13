import React, { useCallback, useEffect, useRef } from 'react';
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { EnhancedDropdown } from './EnhancedDropdown';
import { Tooltip } from './Tooltip';
import { useMediaQuery } from '../../hooks/useMediaQuery';
import './Pagination.css';

type PaginationVariant = 'default' | 'compact' | 'inline' | 'group';

type PaginationHoldDirection = 'prev' | 'next';

interface PaginationProps {
  currentPage: number;
  totalPages: number;
  onPageChange: (page: number) => void;

  /** Required for 'default' and 'compact' variants; optional for 'inline' */
  totalItems?: number;
  itemsPerPage?: number;

  itemLabel?: string;
  className?: string;
  showCard?: boolean;
  /** Offset to extend pagination to parent edges (default: 1.5rem for Card lg padding) */
  parentPadding?: 'sm' | 'md' | 'lg' | 'none';
  /** Variant selector. 'inline' is the minimal chevrons-only layout. */
  variant?: PaginationVariant;
  /** @deprecated Use variant='compact' instead. Kept for backward compatibility. */
  compact?: boolean;
  /** Total number of downloads to display alongside pagination info */
  totalDownloads?: number;
  /** Enable long-press hold-to-repeat on prev/next chevrons (inline variant). */
  holdToRepeat?: boolean;
  /**
   * Optional external hold handlers. When provided together with holdToRepeat,
   * they are used instead of the component's internal timer. Useful when the
   * caller already owns a shared hold timer (e.g. useGroupPagination).
   */
  onPointerHoldStart?: (
    event: React.PointerEvent<HTMLButtonElement>,
    direction: PaginationHoldDirection
  ) => void;
  onPointerHoldEnd?: (event: React.PointerEvent<HTMLButtonElement>) => void;
  onLostPointerCapture?: (event: React.PointerEvent<HTMLButtonElement>) => void;
  /** Tooltip / aria-label text for previous chevron (inline variant) */
  previousLabel?: string;
  /** Tooltip / aria-label text for next chevron (inline variant) */
  nextLabel?: string;
}

const HOLD_INITIAL_DELAY_MS = 350;
const HOLD_REPEAT_INTERVAL_MS = 120;

export const Pagination: React.FC<PaginationProps> = React.memo(
  ({
    currentPage,
    totalPages,
    totalItems = 0,
    itemsPerPage = 0,
    onPageChange,
    itemLabel = 'items',
    className = '',
    showCard = true,
    parentPadding = 'lg',
    variant,
    compact = false,
    totalDownloads,
    holdToRepeat = false,
    onPointerHoldStart,
    onPointerHoldEnd,
    onLostPointerCapture,
    previousLabel,
    nextLabel
  }) => {
    const { t } = useTranslation();
    const isMobile = useMediaQuery('(max-width: 639px)');

    // Resolve final variant. Backward compatibility: compact prop forces compact
    // variant when no explicit variant is provided.
    const resolvedVariant: PaginationVariant = variant ?? (compact ? 'compact' : 'default');
    // Force compact on mobile unless caller explicitly asked for inline.
    const effectiveVariant: PaginationVariant =
      resolvedVariant === 'inline' || resolvedVariant === 'group'
        ? resolvedVariant
        : isMobile || resolvedVariant === 'compact'
          ? 'compact'
          : 'default';

    // CSS class for parent padding offset (applied via CSS custom property)
    const paddingValues: Record<NonNullable<PaginationProps['parentPadding']>, string> = {
      none: '0',
      sm: '0.75rem', // p-3
      md: '1rem', // p-4
      lg: '1.5rem' // p-6
    };
    const offset = paddingValues[parentPadding];

    // Auto-clamp currentPage when it exceeds totalPages (e.g. after filter/search reduces results)
    useEffect(() => {
      const safeMax = Math.max(1, totalPages);
      if (totalPages === 0 && currentPage === 0) return;
      if (currentPage > safeMax) {
        onPageChange(safeMax);
      }
    }, [currentPage, totalPages, onPageChange]);

    // ---- Internal hold-to-repeat timer (used when holdToRepeat && no external callbacks) ----
    const holdTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const holdIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

    const stopInternalHoldTimer = useCallback((): void => {
      if (holdTimeoutRef.current !== null) {
        clearTimeout(holdTimeoutRef.current);
        holdTimeoutRef.current = null;
      }
      if (holdIntervalRef.current !== null) {
        clearInterval(holdIntervalRef.current);
        holdIntervalRef.current = null;
      }
    }, []);

    useEffect(() => {
      return (): void => {
        stopInternalHoldTimer();
      };
    }, [stopInternalHoldTimer]);

    const internalPointerHoldStart = useCallback(
      (event: React.PointerEvent<HTMLButtonElement>, direction: PaginationHoldDirection): void => {
        const isPrevious = direction === 'prev';
        if ((isPrevious && currentPage === 1) || (!isPrevious && currentPage === totalPages)) {
          return;
        }
        event.currentTarget.setPointerCapture?.(event.pointerId);
        stopInternalHoldTimer();
        const step = (): void => {
          const next = isPrevious
            ? Math.max(1, currentPage - 1)
            : Math.min(totalPages, currentPage + 1);
          if (next !== currentPage) {
            onPageChange(next);
          }
        };
        holdTimeoutRef.current = setTimeout(() => {
          holdIntervalRef.current = setInterval(step, HOLD_REPEAT_INTERVAL_MS);
        }, HOLD_INITIAL_DELAY_MS);
      },
      [currentPage, totalPages, onPageChange, stopInternalHoldTimer]
    );

    const internalPointerHoldEnd = useCallback(
      (event: React.PointerEvent<HTMLButtonElement>): void => {
        event.currentTarget.releasePointerCapture?.(event.pointerId);
        stopInternalHoldTimer();
      },
      [stopInternalHoldTimer]
    );

    // Resolved pointer handlers for the inline variant. Prefer external when provided.
    const useExternalHold = holdToRepeat && !!onPointerHoldStart;
    const useInternalHold = holdToRepeat && !onPointerHoldStart;

    const resolvedHoldStart = useExternalHold
      ? onPointerHoldStart
      : useInternalHold
        ? internalPointerHoldStart
        : undefined;
    const resolvedHoldEnd = useExternalHold
      ? onPointerHoldEnd
      : useInternalHold
        ? internalPointerHoldEnd
        : undefined;
    const resolvedLostCapture = useExternalHold
      ? (onLostPointerCapture ?? onPointerHoldEnd)
      : useInternalHold
        ? internalPointerHoldEnd
        : undefined;

    if (totalPages <= 1) return null;

    const startItem = (currentPage - 1) * itemsPerPage + 1;
    const endItem = Math.min(currentPage * itemsPerPage, totalItems);

    // ------ Group variant (ex-GroupPagination default: centered, medium chevrons) ------
    if (effectiveVariant === 'group') {
      const prevDisabled = currentPage === 1;
      const nextDisabled = currentPage === totalPages;
      const prevTitle = previousLabel ?? t('ui.pagination.previousPage');
      const nextTitle = nextLabel ?? t('ui.pagination.nextPage');

      const handlePrevPointerDown = resolvedHoldStart
        ? (event: React.PointerEvent<HTMLButtonElement>): void => resolvedHoldStart(event, 'prev')
        : undefined;
      const handleNextPointerDown = resolvedHoldStart
        ? (event: React.PointerEvent<HTMLButtonElement>): void => resolvedHoldStart(event, 'next')
        : undefined;

      return (
        <div className={`pagination-group ${className}`.trim()}>
          <Tooltip content={prevTitle}>
            <button
              type="button"
              onClick={() => onPageChange(currentPage - 1)}
              onPointerDown={handlePrevPointerDown}
              onPointerUp={resolvedHoldEnd}
              onPointerCancel={resolvedHoldEnd}
              onLostPointerCapture={resolvedLostCapture}
              disabled={prevDisabled}
              className="pagination-group__btn"
              aria-label={prevTitle}
            >
              <ChevronLeft size={16} />
            </button>
          </Tooltip>

          <span className="pagination-group__label">
            {currentPage} of {totalPages}
          </span>

          <Tooltip content={nextTitle}>
            <button
              type="button"
              onClick={() => onPageChange(currentPage + 1)}
              onPointerDown={handleNextPointerDown}
              onPointerUp={resolvedHoldEnd}
              onPointerCancel={resolvedHoldEnd}
              onLostPointerCapture={resolvedLostCapture}
              disabled={nextDisabled}
              className="pagination-group__btn"
              aria-label={nextTitle}
            >
              <ChevronRight size={16} />
            </button>
          </Tooltip>
        </div>
      );
    }

    // ------ Inline variant (ex-GroupPagination inline: chevrons + "N/M", compact) ------
    if (effectiveVariant === 'inline') {
      const prevDisabled = currentPage === 1;
      const nextDisabled = currentPage === totalPages;
      const prevTitle = previousLabel ?? t('ui.pagination.previousPage');
      const nextTitle = nextLabel ?? t('ui.pagination.nextPage');

      const handlePrevPointerDown = resolvedHoldStart
        ? (event: React.PointerEvent<HTMLButtonElement>): void => resolvedHoldStart(event, 'prev')
        : undefined;
      const handleNextPointerDown = resolvedHoldStart
        ? (event: React.PointerEvent<HTMLButtonElement>): void => resolvedHoldStart(event, 'next')
        : undefined;

      return (
        <div className={`pagination-inline ${className}`.trim()}>
          <Tooltip content={prevTitle}>
            <button
              type="button"
              onClick={() => onPageChange(currentPage - 1)}
              onPointerDown={handlePrevPointerDown}
              onPointerUp={resolvedHoldEnd}
              onPointerCancel={resolvedHoldEnd}
              onLostPointerCapture={resolvedLostCapture}
              disabled={prevDisabled}
              className="pagination-inline__btn"
              aria-label={prevTitle}
            >
              <ChevronLeft size={12} />
            </button>
          </Tooltip>

          <span className="pagination-inline__label">
            {currentPage}/{totalPages}
          </span>

          <Tooltip content={nextTitle}>
            <button
              type="button"
              onClick={() => onPageChange(currentPage + 1)}
              onPointerDown={handleNextPointerDown}
              onPointerUp={resolvedHoldEnd}
              onPointerCancel={resolvedHoldEnd}
              onLostPointerCapture={resolvedLostCapture}
              disabled={nextDisabled}
              className="pagination-inline__btn"
              aria-label={nextTitle}
            >
              <ChevronRight size={12} />
            </button>
          </Tooltip>
        </div>
      );
    }

    // ------ Compact variant (unchanged behaviour) ------
    if (effectiveVariant === 'compact') {
      const prevDisabled = currentPage === 1;
      const nextDisabled = currentPage === totalPages;
      const prevTitle = previousLabel ?? t('ui.pagination.previousPage');
      const nextTitle = nextLabel ?? t('ui.pagination.nextPage');

      const handlePrevPointerDown = resolvedHoldStart
        ? (event: React.PointerEvent<HTMLButtonElement>): void => resolvedHoldStart(event, 'prev')
        : undefined;
      const handleNextPointerDown = resolvedHoldStart
        ? (event: React.PointerEvent<HTMLButtonElement>): void => resolvedHoldStart(event, 'next')
        : undefined;

      const compactContent = (
        <div className={`flex items-center justify-between gap-2 ${!showCard ? className : ''}`}>
          {/* Page info */}
          {itemsPerPage > 0 && totalItems > 0 && (
            <span className="text-xs text-themed-muted">
              {startItem}-{endItem} of {totalItems}
            </span>
          )}

          {/* Navigation */}
          <div className="flex items-center gap-1">
            <button
              onClick={() => onPageChange(Math.max(1, currentPage - 1))}
              onPointerDown={handlePrevPointerDown}
              onPointerUp={resolvedHoldEnd}
              onPointerCancel={resolvedHoldEnd}
              onLostPointerCapture={resolvedLostCapture}
              disabled={prevDisabled}
              className="p-1.5 rounded-md transition-all disabled:opacity-40 disabled:cursor-not-allowed bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-secondary)]"
              title={prevTitle}
            >
              <ChevronLeft size={14} />
            </button>

            <span className="text-xs font-medium px-2 tabular-nums text-themed-primary">
              {currentPage}/{totalPages}
            </span>

            <button
              onClick={() => onPageChange(Math.min(totalPages, currentPage + 1))}
              onPointerDown={handleNextPointerDown}
              onPointerUp={resolvedHoldEnd}
              onPointerCancel={resolvedHoldEnd}
              onLostPointerCapture={resolvedLostCapture}
              disabled={nextDisabled}
              className="p-1.5 rounded-md transition-all disabled:opacity-40 disabled:cursor-not-allowed bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-secondary)]"
              title={nextTitle}
            >
              <ChevronRight size={14} />
            </button>
          </div>
        </div>
      );

      if (!showCard) {
        return compactContent;
      }

      return (
        <div
          className={`pagination-card-bleed relative mt-4 z-20 pt-3 bg-[var(--theme-card-bg)] border-t border-[var(--theme-border-primary)] rounded-b-xl overflow-x-auto ${className}`}
          style={{ '--pagination-offset': offset } as React.CSSProperties}
        >
          {compactContent}
        </div>
      );
    }

    // ------ Default variant ------
    const content = (
      <div
        className={`flex flex-col sm:flex-row items-center justify-between gap-3 ${!showCard ? className : ''}`}
      >
        {/* Page Info */}
        <div className="flex items-center gap-4">
          <span className="text-sm font-medium text-themed-primary">
            {t('ui.pagination.pageInfo', { current: currentPage, total: totalPages })}
          </span>
          {itemsPerPage > 0 && totalItems > 0 && (
            <span className="text-sm text-themed-secondary">
              {t('ui.pagination.itemRange', {
                start: startItem,
                end: endItem,
                total: totalItems,
                label: itemLabel
              })}
            </span>
          )}
          {totalDownloads !== undefined && (
            <span className="text-sm text-themed-muted">
              {t('ui.pagination.downloadCount', { count: totalDownloads })}
            </span>
          )}
        </div>

        {/* Navigation Controls */}
        <div className="flex items-center gap-2">
          {/* First Page */}
          <button
            onClick={() => onPageChange(1)}
            disabled={currentPage === 1}
            className="p-2 rounded-lg transition-[transform,box-shadow] hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100 bg-[var(--theme-bg-secondary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-primary)]"
            title={t('ui.pagination.firstPage')}
            aria-label={t('ui.pagination.goToFirstPage')}
          >
            <ChevronsLeft size={16} />
          </button>

          {/* Previous Page */}
          <button
            onClick={() => onPageChange(Math.max(1, currentPage - 1))}
            disabled={currentPage === 1}
            className="p-2 rounded-lg transition-[transform,box-shadow] hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100 bg-[var(--theme-bg-secondary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-primary)]"
            title={t('ui.pagination.previousPage')}
            aria-label={t('ui.pagination.goToPreviousPage')}
          >
            <ChevronLeft size={16} />
          </button>

          {/* Page Numbers Container */}
          <div className="flex items-center gap-1 px-2">
            {totalPages <= 7 ? (
              Array.from({ length: totalPages }, (_, i) => i + 1).map((pageNum) => (
                <button
                  key={pageNum}
                  onClick={() => onPageChange(pageNum)}
                  className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-[transform,box-shadow] hover:scale-105 ${
                    currentPage === pageNum ? 'shadow-md' : 'hover:bg-opacity-80'
                  }`}
                  style={{
                    backgroundColor:
                      currentPage === pageNum ? 'var(--theme-primary)' : 'var(--theme-bg-tertiary)',
                    color:
                      currentPage === pageNum
                        ? 'var(--theme-button-text)'
                        : 'var(--theme-text-primary)',
                    border:
                      currentPage === pageNum
                        ? '1px solid var(--theme-primary)'
                        : '1px solid var(--theme-border-secondary)'
                  }}
                  aria-label={t('ui.pagination.goToPage', { page: pageNum })}
                  aria-current={currentPage === pageNum ? 'page' : undefined}
                >
                  {pageNum}
                </button>
              ))
            ) : (
              <>
                <button
                  onClick={() => onPageChange(1)}
                  className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-[transform,box-shadow] hover:scale-105 ${
                    currentPage === 1 ? 'shadow-md' : 'hover:bg-opacity-80'
                  }`}
                  style={{
                    backgroundColor:
                      currentPage === 1 ? 'var(--theme-primary)' : 'var(--theme-bg-tertiary)',
                    color:
                      currentPage === 1 ? 'var(--theme-button-text)' : 'var(--theme-text-primary)',
                    border:
                      currentPage === 1
                        ? '1px solid var(--theme-primary)'
                        : '1px solid var(--theme-border-secondary)'
                  }}
                  aria-label={t('ui.pagination.goToPage', { page: 1 })}
                  aria-current={currentPage === 1 ? 'page' : undefined}
                >
                  1
                </button>

                {currentPage > 3 && (
                  <button
                    onClick={() => onPageChange(Math.max(1, currentPage - 5))}
                    className="min-w-[32px] h-8 px-2 rounded-lg font-medium transition-[transform,box-shadow] hover:scale-105 hover:bg-opacity-80 cursor-pointer"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      color: 'var(--theme-text-secondary)',
                      border: '1px solid var(--theme-border-secondary)'
                    }}
                    title={t('ui.pagination.jumpBack', { count: 5 })}
                    aria-label={t('ui.pagination.jumpBack', { count: 5 })}
                  >
                    •••
                  </button>
                )}

                {Array.from({ length: 5 }, (_, i) => {
                  const pageNum = currentPage - 2 + i;
                  if (pageNum <= 1 || pageNum >= totalPages) return null;
                  return (
                    <button
                      key={pageNum}
                      onClick={() => onPageChange(pageNum)}
                      className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-[transform,box-shadow] hover:scale-105 ${
                        currentPage === pageNum ? 'shadow-md' : 'hover:bg-opacity-80'
                      }`}
                      style={{
                        backgroundColor:
                          currentPage === pageNum
                            ? 'var(--theme-primary)'
                            : 'var(--theme-bg-tertiary)',
                        color:
                          currentPage === pageNum
                            ? 'var(--theme-button-text)'
                            : 'var(--theme-text-primary)',
                        border:
                          currentPage === pageNum
                            ? '1px solid var(--theme-primary)'
                            : '1px solid var(--theme-border-secondary)'
                      }}
                      aria-label={t('ui.pagination.goToPage', { page: pageNum })}
                      aria-current={currentPage === pageNum ? 'page' : undefined}
                    >
                      {pageNum}
                    </button>
                  );
                }).filter(Boolean)}

                {currentPage < totalPages - 2 && (
                  <button
                    onClick={() => onPageChange(Math.min(totalPages, currentPage + 5))}
                    className="min-w-[32px] h-8 px-2 rounded-lg font-medium transition-[transform,box-shadow] hover:scale-105 hover:bg-opacity-80 cursor-pointer"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      color: 'var(--theme-text-secondary)',
                      border: '1px solid var(--theme-border-secondary)'
                    }}
                    title={t('ui.pagination.jumpForward', { count: 5 })}
                    aria-label={t('ui.pagination.jumpForward', { count: 5 })}
                  >
                    •••
                  </button>
                )}

                <button
                  onClick={() => onPageChange(totalPages)}
                  className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-[transform,box-shadow] hover:scale-105 ${
                    currentPage === totalPages ? 'shadow-md' : 'hover:bg-opacity-80'
                  }`}
                  style={{
                    backgroundColor:
                      currentPage === totalPages
                        ? 'var(--theme-primary)'
                        : 'var(--theme-bg-tertiary)',
                    color:
                      currentPage === totalPages
                        ? 'var(--theme-button-text)'
                        : 'var(--theme-text-primary)',
                    border:
                      currentPage === totalPages
                        ? '1px solid var(--theme-primary)'
                        : '1px solid var(--theme-border-secondary)'
                  }}
                  aria-label={t('ui.pagination.goToPage', { page: totalPages })}
                  aria-current={currentPage === totalPages ? 'page' : undefined}
                >
                  {totalPages}
                </button>
              </>
            )}
          </div>

          {/* Next Page */}
          <button
            onClick={() => onPageChange(Math.min(totalPages, currentPage + 1))}
            disabled={currentPage === totalPages}
            className="p-2 rounded-lg transition-[transform,box-shadow] hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100 bg-[var(--theme-bg-secondary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-primary)]"
            title={t('ui.pagination.nextPage')}
            aria-label={t('ui.pagination.goToNextPage')}
          >
            <ChevronRight size={16} />
          </button>

          {/* Last Page */}
          <button
            onClick={() => onPageChange(totalPages)}
            disabled={currentPage === totalPages}
            className="p-2 rounded-lg transition-[transform,box-shadow] hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100 bg-[var(--theme-bg-secondary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-primary)]"
            title={t('ui.pagination.lastPage')}
            aria-label={t('ui.pagination.goToLastPage')}
          >
            <ChevronsRight size={16} />
          </button>

          {/* Quick Page Jump (for many pages) */}
          {totalPages > 10 && (
            <>
              <div className="border-l mx-2 h-6 border-themed-secondary" />
              <EnhancedDropdown
                options={Array.from({ length: totalPages }, (_, i) => ({
                  value: (i + 1).toString(),
                  label: t('ui.pagination.page') + ' ' + (i + 1)
                }))}
                value={currentPage.toString()}
                onChange={(value) => onPageChange(parseInt(value))}
                placeholder={t('ui.pagination.jumpTo')}
                className="w-32"
              />
            </>
          )}
        </div>
      </div>
    );

    if (!showCard) {
      return content;
    }

    return (
      <div
        className={`pagination-card-bleed relative mt-4 z-20 pt-4 bg-[var(--theme-card-bg)] border-t border-[var(--theme-border-primary)] rounded-b-xl overflow-x-auto ${className}`}
        style={{ '--pagination-offset': offset } as React.CSSProperties}
      >
        {content}
      </div>
    );
  }
);

Pagination.displayName = 'Pagination';
