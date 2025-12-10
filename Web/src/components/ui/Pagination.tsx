import React from 'react';
import {
  ChevronLeft,
  ChevronRight,
  ChevronsLeft,
  ChevronsRight
} from 'lucide-react';
import { EnhancedDropdown } from './EnhancedDropdown';
import { Card } from './Card';

interface PaginationProps {
  currentPage: number;
  totalPages: number;
  totalItems: number;
  itemsPerPage: number;
  onPageChange: (page: number) => void;
  itemLabel?: string;
  className?: string;
  showCard?: boolean;
}

export const Pagination: React.FC<PaginationProps> = ({
  currentPage,
  totalPages,
  totalItems,
  itemsPerPage,
  onPageChange,
  itemLabel = 'items',
  className = '',
  showCard = true
}) => {
  if (totalPages <= 1) return null;

  const startItem = (currentPage - 1) * itemsPerPage + 1;
  const endItem = Math.min(currentPage * itemsPerPage, totalItems);

  const content = (
    <div className={`flex flex-col sm:flex-row items-center justify-between gap-3 ${!showCard ? className : ''}`}>
      {/* Page Info */}
      <div className="flex items-center gap-4">
        <span
          className="text-sm font-medium"
          style={{ color: 'var(--theme-text-primary)' }}
        >
          Page {currentPage} of {totalPages}
        </span>
        <span className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
          {startItem} - {endItem} of {totalItems} {itemLabel}
        </span>
      </div>

      {/* Navigation Controls */}
      <div className="flex items-center gap-2">
        {/* First Page */}
        <button
          onClick={() => onPageChange(1)}
          disabled={currentPage === 1}
          className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            color: 'var(--theme-text-primary)',
            border: '1px solid var(--theme-border-primary)'
          }}
          title="First page"
          aria-label="Go to first page"
        >
          <ChevronsLeft size={16} />
        </button>

        {/* Previous Page */}
        <button
          onClick={() => onPageChange(Math.max(1, currentPage - 1))}
          disabled={currentPage === 1}
          className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            color: 'var(--theme-text-primary)',
            border: '1px solid var(--theme-border-primary)'
          }}
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
                <span className="px-2" style={{ color: 'var(--theme-text-muted)' }}>
                  •••
                </span>
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
                <span className="px-2" style={{ color: 'var(--theme-text-muted)' }}>
                  •••
                </span>
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
          className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            color: 'var(--theme-text-primary)',
            border: '1px solid var(--theme-border-primary)'
          }}
          title="Next page"
          aria-label="Go to next page"
        >
          <ChevronRight size={16} />
        </button>

        {/* Last Page */}
        <button
          onClick={() => onPageChange(totalPages)}
          disabled={currentPage === totalPages}
          className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            color: 'var(--theme-text-primary)',
            border: '1px solid var(--theme-border-primary)'
          }}
          title="Last page"
          aria-label="Go to last page"
        >
          <ChevronsRight size={16} />
        </button>

        {/* Quick Page Jump (for many pages) */}
        {totalPages > 10 && (
          <>
            <div
              className="border-l mx-2 h-6"
              style={{ borderColor: 'var(--theme-border-secondary)' }}
            />
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
      className={`sticky bottom-0 mt-4 z-20 rounded-lg ${className}`}
      style={{
        backgroundColor: 'var(--theme-bg-primary)',
        paddingTop: '8px',
        paddingBottom: '8px',
        boxShadow: '0 -4px 12px rgba(0,0,0,0.1)',
        borderRadius: 'var(--theme-border-radius-lg, 0.75rem)'
      }}
    >
      <Card padding="sm" className="max-w-4xl mx-auto">
        {content}
      </Card>
    </div>
  );
};

export default Pagination;
