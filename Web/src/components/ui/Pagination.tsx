import React, { useCallback, useEffect, useRef } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { EnhancedDropdown } from './EnhancedDropdown';
import { Tooltip } from './Tooltip';
import { useMediaQuery } from '../../hooks/useMediaQuery';
import './Pagination.css';

type PaginationVariant = 'default' | 'compact' | 'inline' | 'group';

type PaginationHoldDirection = 'prev' | 'next';

type PageSlot = number | 'gap-back' | 'gap-forward';

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
  /** Pager bar size: 'md' matches the md control height, 'xs' is the small bar.
      Defaults to 'md' for the default variant and 'xs' for compact. */
  size?: 'xs' | 'md';
  /** Offset to extend pagination to parent edges (default: 1.5rem for Card lg padding) */
  parentPadding?: 'sm' | 'md' | 'lg' | 'none';
  /** Variant selector. 'inline' is the minimal chevrons-only layout. */
  variant?: PaginationVariant;
  /** @deprecated Use variant='compact' instead. Kept for backward compatibility. */
  compact?: boolean;
  /** Total number of downloads to display alongside pagination info */
  totalDownloads?: number;
  /** Enable long-press hold-to-repeat on prev/next chevrons. */
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
const GAP_JUMP_PAGES = 5;

/**
 * Fixed-width page window: always exactly 7 slots when totalPages > 7, so cells
 * never appear or disappear while paging and the chevrons keep their position.
 */
const buildPageSlots = (currentPage: number, totalPages: number): PageSlot[] => {
  if (totalPages <= 7) {
    return Array.from({ length: totalPages }, (_, index: number): number => index + 1);
  }
  if (currentPage <= 4) {
    return [1, 2, 3, 4, 5, 'gap-forward', totalPages];
  }
  if (currentPage >= totalPages - 3) {
    return [
      1,
      'gap-back',
      totalPages - 4,
      totalPages - 3,
      totalPages - 2,
      totalPages - 1,
      totalPages
    ];
  }
  return [1, 'gap-back', currentPage - 1, currentPage, currentPage + 1, 'gap-forward', totalPages];
};

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
    size,
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
    const currentPageRef = useRef(currentPage);
    currentPageRef.current = currentPage;

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
          const page = currentPageRef.current;
          const next = isPrevious ? Math.max(1, page - 1) : Math.min(totalPages, page + 1);
          if (next !== page) {
            onPageChange(next);
          } else {
            stopInternalHoldTimer();
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

    // Resolved pointer handlers. Prefer external when provided.
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

    const resolvedSize: 'xs' | 'md' = size ?? (effectiveVariant === 'compact' ? 'xs' : 'md');
    const barClass = resolvedSize === 'xs' ? 'pagination-bar pagination-bar--xs' : 'pagination-bar';

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

    // ------ Group variant (centered, medium chevrons) ------
    if (effectiveVariant === 'group') {
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
              className="pagination-group__btn focus-ring--inset"
              aria-label={prevTitle}
            >
              <ChevronLeft size={16} />
            </button>
          </Tooltip>

          <span className="pagination-group__label">
            <span className="pagination-ghost" aria-hidden="true">
              {t('ui.pagination.pageOfTotal', { current: totalPages, total: totalPages })}
            </span>
            <span>
              {t('ui.pagination.pageOfTotal', { current: currentPage, total: totalPages })}
            </span>
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
              className="pagination-group__btn focus-ring--inset"
              aria-label={nextTitle}
            >
              <ChevronRight size={16} />
            </button>
          </Tooltip>
        </div>
      );
    }

    // ------ Inline variant (chevrons + "N/M", compact) ------
    if (effectiveVariant === 'inline') {
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
              className="pagination-inline__btn focus-ring--inset"
              aria-label={prevTitle}
            >
              <ChevronLeft size={12} />
            </button>
          </Tooltip>

          <span className="pagination-inline__label">
            <span className="pagination-ghost" aria-hidden="true">
              {totalPages}/{totalPages}
            </span>
            <span>
              {currentPage}/{totalPages}
            </span>
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
              className="pagination-inline__btn focus-ring--inset"
              aria-label={nextTitle}
            >
              <ChevronRight size={12} />
            </button>
          </Tooltip>
        </div>
      );
    }

    // ------ Compact variant ------
    if (effectiveVariant === 'compact') {
      const compactContent = (
        <div className={`pagination-row ${!showCard ? className : ''}`.trim()}>
          {itemsPerPage > 0 && totalItems > 0 && (
            <span className="pagination-info">
              {t('ui.pagination.itemRange', {
                start: startItem,
                end: endItem,
                total: totalItems,
                label: itemLabel
              })}
            </span>
          )}

          <div className={barClass}>
            <Tooltip content={prevTitle} position="top">
              <button
                type="button"
                onClick={() => onPageChange(Math.max(1, currentPage - 1))}
                onPointerDown={handlePrevPointerDown}
                onPointerUp={resolvedHoldEnd}
                onPointerCancel={resolvedHoldEnd}
                onLostPointerCapture={resolvedLostCapture}
                disabled={prevDisabled}
                className="pagination-bar__btn focus-ring--inset"
                aria-label={prevTitle}
              >
                <ChevronLeft size={14} />
              </button>
            </Tooltip>

            <span className="pagination-bar__label tabular-nums">
              <span className="pagination-ghost" aria-hidden="true">
                {totalPages}/{totalPages}
              </span>
              <span>
                {currentPage}/{totalPages}
              </span>
            </span>

            <Tooltip content={nextTitle} position="top">
              <button
                type="button"
                onClick={() => onPageChange(Math.min(totalPages, currentPage + 1))}
                onPointerDown={handleNextPointerDown}
                onPointerUp={resolvedHoldEnd}
                onPointerCancel={resolvedHoldEnd}
                onLostPointerCapture={resolvedLostCapture}
                disabled={nextDisabled}
                className="pagination-bar__btn focus-ring--inset"
                aria-label={nextTitle}
              >
                <ChevronRight size={14} />
              </button>
            </Tooltip>
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
    const slots = buildPageSlots(currentPage, totalPages);

    const content = (
      <div className={`pagination-row ${!showCard ? className : ''}`.trim()}>
        <div className="pagination-info">
          <span>
            {itemsPerPage > 0 && totalItems > 0
              ? t('ui.pagination.itemRange', {
                  start: startItem,
                  end: endItem,
                  total: totalItems,
                  label: itemLabel
                })
              : t('ui.pagination.pageInfo', { current: currentPage, total: totalPages })}
          </span>
          {totalDownloads !== undefined && (
            <span className="pagination-info__muted">
              {t('ui.pagination.downloadCount', { count: totalDownloads })}
            </span>
          )}
        </div>

        <div className="pagination-controls">
          <div className={barClass}>
            <Tooltip content={prevTitle} position="top">
              <button
                type="button"
                onClick={() => onPageChange(Math.max(1, currentPage - 1))}
                onPointerDown={handlePrevPointerDown}
                onPointerUp={resolvedHoldEnd}
                onPointerCancel={resolvedHoldEnd}
                onLostPointerCapture={resolvedLostCapture}
                disabled={prevDisabled}
                className="pagination-bar__btn focus-ring--inset"
                aria-label={t('ui.pagination.goToPreviousPage')}
              >
                <ChevronLeft size={16} />
              </button>
            </Tooltip>

            {slots.map((slot: PageSlot) => {
              if (slot === 'gap-back' || slot === 'gap-forward') {
                const isBack = slot === 'gap-back';
                const jumpLabel = isBack
                  ? t('ui.pagination.jumpBack', { count: GAP_JUMP_PAGES })
                  : t('ui.pagination.jumpForward', { count: GAP_JUMP_PAGES });
                const target = isBack
                  ? Math.max(1, currentPage - GAP_JUMP_PAGES)
                  : Math.min(totalPages, currentPage + GAP_JUMP_PAGES);
                return (
                  <Tooltip key={slot} content={jumpLabel} position="top">
                    <button
                      type="button"
                      onClick={() => onPageChange(target)}
                      className="pagination-bar__btn pagination-bar__btn--gap focus-ring--inset"
                      aria-label={jumpLabel}
                    >
                      …
                    </button>
                  </Tooltip>
                );
              }
              return (
                <button
                  key={slot}
                  type="button"
                  onClick={() => onPageChange(slot)}
                  className={`pagination-bar__btn pagination-bar__btn--num tabular-nums focus-ring--inset${
                    slot === currentPage ? ' pagination-bar__btn--active' : ''
                  }`}
                  aria-label={t('ui.pagination.goToPage', { page: slot })}
                  aria-current={slot === currentPage ? 'page' : undefined}
                >
                  {slot}
                </button>
              );
            })}

            <Tooltip content={nextTitle} position="top">
              <button
                type="button"
                onClick={() => onPageChange(Math.min(totalPages, currentPage + 1))}
                onPointerDown={handleNextPointerDown}
                onPointerUp={resolvedHoldEnd}
                onPointerCancel={resolvedHoldEnd}
                onLostPointerCapture={resolvedLostCapture}
                disabled={nextDisabled}
                className="pagination-bar__btn focus-ring--inset"
                aria-label={t('ui.pagination.goToNextPage')}
              >
                <ChevronRight size={16} />
              </button>
            </Tooltip>
          </div>

          {totalPages > 10 && (
            <EnhancedDropdown
              options={Array.from({ length: totalPages }, (_, index: number) => ({
                value: (index + 1).toString(),
                label: t('ui.pagination.page') + ' ' + (index + 1)
              }))}
              value={currentPage.toString()}
              onChange={(value: string) => onPageChange(parseInt(value, 10))}
              placeholder={t('ui.pagination.jumpTo')}
              className="w-32"
            />
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
