import React from 'react';
import {
  ChevronLeft,
  ChevronRight,
  ChevronsLeft,
  ChevronsRight
} from 'lucide-react';
import { EnhancedDropdown } from './EnhancedDropdown';

interface PaginationProps {
  currentPage: number;
  totalPages: number;
  totalItems: number;
  itemsPerPage: number;
  onPageChange: (page: number) => void;
  itemLabel?: string;
  className?: string;
  showCard?: boolean;
  /** Offset to extend pagination to parent edges (default: 1.5rem for Card lg padding) */
  parentPadding?: 'sm' | 'md' | 'lg' | 'none';
  /** Use compact mode for narrow containers - shows only prev/next with page indicator */
  compact?: boolean;
}

export const Pagination: React.FC<PaginationProps> = ({
  currentPage,
  totalPages,
  totalItems,
  itemsPerPage,
  onPageChange,
  itemLabel = 'items',
  className = '',
  showCard = true,
  parentPadding = 'lg',
  compact = false
}) => {
  // Calculate offset based on parent padding
  const paddingValues = {
    none: '0',
    sm: '0.75rem',  // p-3
    md: '1rem',     // p-4
    lg: '1.5rem'    // p-6
  };
  const offset = paddingValues[parentPadding];
  if (totalPages <= 1) return null;

  const startItem = (currentPage - 1) * itemsPerPage + 1;
  const endItem = Math.min(currentPage * itemsPerPage, totalItems);

  // Compact mode - simplified layout for narrow containers
  if (compact) {
    const compactContent = (
      <div className={`flex items-center justify-between gap-2 ${!showCard ? className : ''}`}>
        {/* Page info */}
        <span className="text-xs text-themed-muted">
          {startItem}-{endItem} of {totalItems}
        </span>

        {/* Navigation */}
        <div className="flex items-center gap-1">
          <button
            onClick={() => onPageChange(Math.max(1, currentPage - 1))}
            disabled={currentPage === 1}
            className="p-1.5 rounded-md transition-all disabled:opacity-40 disabled:cursor-not-allowed bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-secondary)]"
            title="Previous page"
          >
            <ChevronLeft size={14} />
          </button>

          <span className="text-xs font-medium px-2 tabular-nums text-themed-primary">
            {currentPage}/{totalPages}
          </span>

          <button
            onClick={() => onPageChange(Math.min(totalPages, currentPage + 1))}
            disabled={currentPage === totalPages}
            className="p-1.5 rounded-md transition-all disabled:opacity-40 disabled:cursor-not-allowed bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-secondary)]"
            title="Next page"
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
        className={`relative mt-4 z-20 pt-3 bg-[var(--theme-card-bg)] border-t border-[var(--theme-border-primary)] rounded-b-xl ${className}`}
        style={{
          marginLeft: `-${offset}`,
          marginRight: `-${offset}`,
          marginBottom: `-${offset}`,
          paddingLeft: offset,
          paddingRight: offset,
          paddingBottom: offset
        }}
      >
        {compactContent}
      </div>
    );
  }

  const content = (
    <div className={`flex flex-col sm:flex-row items-center justify-between gap-3 ${!showCard ? className : ''}`}>
      {/* Page Info */}
      <div className="flex items-center gap-4">
        <span className="text-sm font-medium text-themed-primary">
          Page {currentPage} of {totalPages}
        </span>
        <span className="text-sm text-themed-secondary">
          {startItem} - {endItem} of {totalItems} {itemLabel}
        </span>
      </div>

      {/* Navigation Controls */}
      <div className="flex items-center gap-2">
        {/* First Page */}
        <button
          onClick={() => onPageChange(1)}
          disabled={currentPage === 1}
          className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100 bg-[var(--theme-bg-secondary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-primary)]"
          title="First page"
          aria-label="Go to first page"
        >
          <ChevronsLeft size={16} />
        </button>

        {/* Previous Page */}
        <button
          onClick={() => onPageChange(Math.max(1, currentPage - 1))}
          disabled={currentPage === 1}
          className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100 bg-[var(--theme-bg-secondary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-primary)]"
          title="Previous page"
          aria-label="Go to previous page"
        >
          <ChevronLeft size={16} />
        </button>

        {/* Page Numbers Container */}
        <div className="flex items-center gap-1 px-2">
          {/* For small number of pages, show all */}
          {totalPages <= 7 ? (
            Array.from({ length: totalPages }, (_, i) => i + 1).map((pageNum) => (
              <button
                key={pageNum}
                onClick={() => onPageChange(pageNum)}
                className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
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
                aria-label={`Go to page ${pageNum}`}
                aria-current={currentPage === pageNum ? 'page' : undefined}
              >
                {pageNum}
              </button>
            ))
          ) : (
            <>
              {/* Complex pagination for many pages */}
              <button
                onClick={() => onPageChange(1)}
                className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
                  currentPage === 1 ? 'shadow-md' : 'hover:bg-opacity-80'
                }`}
                style={{
                  backgroundColor:
                    currentPage === 1 ? 'var(--theme-primary)' : 'var(--theme-bg-tertiary)',
                  color:
                    currentPage === 1
                      ? 'var(--theme-button-text)'
                      : 'var(--theme-text-primary)',
                  border:
                    currentPage === 1
                      ? '1px solid var(--theme-primary)'
                      : '1px solid var(--theme-border-secondary)'
                }}
                aria-label="Go to page 1"
                aria-current={currentPage === 1 ? 'page' : undefined}
              >
                1
              </button>

              {currentPage > 3 && (
                <span className="px-2 text-themed-muted">•••</span>
              )}

              {Array.from({ length: 5 }, (_, i) => {
                const pageNum = currentPage - 2 + i;
                if (pageNum <= 1 || pageNum >= totalPages) return null;
                return (
                  <button
                    key={pageNum}
                    onClick={() => onPageChange(pageNum)}
                    className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
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
                    aria-label={`Go to page ${pageNum}`}
                    aria-current={currentPage === pageNum ? 'page' : undefined}
                  >
                    {pageNum}
                  </button>
                );
              }).filter(Boolean)}

              {currentPage < totalPages - 2 && (
                <span className="px-2 text-themed-muted">•••</span>
              )}

              <button
                onClick={() => onPageChange(totalPages)}
                className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
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
                aria-label={`Go to page ${totalPages}`}
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
          className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100 bg-[var(--theme-bg-secondary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-primary)]"
          title="Next page"
          aria-label="Go to next page"
        >
          <ChevronRight size={16} />
        </button>

        {/* Last Page */}
        <button
          onClick={() => onPageChange(totalPages)}
          disabled={currentPage === totalPages}
          className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100 bg-[var(--theme-bg-secondary)] text-[var(--theme-text-primary)] border border-[var(--theme-border-primary)]"
          title="Last page"
          aria-label="Go to last page"
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
                label: `Page ${i + 1}`
              }))}
              value={currentPage.toString()}
              onChange={(value) => onPageChange(parseInt(value))}
              placeholder="Jump to..."
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
      className={`relative mt-4 z-20 pt-4 bg-[var(--theme-card-bg)] border-t border-[var(--theme-border-primary)] rounded-b-xl ${className}`}
      style={{
        // Use negative margins to extend beyond parent padding and cover rounded corners
        marginLeft: `-${offset}`,
        marginRight: `-${offset}`,
        marginBottom: `-${offset}`,
        paddingLeft: offset,
        paddingRight: offset,
        paddingBottom: offset
      }}
    >
      {content}
    </div>
  );
};

export default Pagination;
