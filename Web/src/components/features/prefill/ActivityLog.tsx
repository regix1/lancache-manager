import { useEffect, useRef, useState, memo, useMemo } from 'react';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import {
  CheckCircle2,
  AlertCircle,
  Info,
  Download,
  LogIn,
  Loader2,
  XCircle,
  Terminal,
  ChevronLeft,
  ChevronRight,
  Activity
} from 'lucide-react';

export type LogEntryType =
  | 'info'
  | 'success'
  | 'error'
  | 'warning'
  | 'download'
  | 'auth'
  | 'progress'
  | 'command';

export interface LogEntry {
  id: string;
  timestamp: Date;
  type: LogEntryType;
  message: string;
  details?: string;
}

interface ActivityLogProps {
  entries: LogEntry[];
  maxHeight?: string;
  className?: string;
}

// Type-to-style mapping for consistent theming
const typeStyles: Record<LogEntryType, { color: string; bgColor: string; icon: typeof Info }> = {
  success: { color: 'var(--theme-success)', bgColor: 'rgba(34, 197, 94, 0.1)', icon: CheckCircle2 },
  error: { color: 'var(--theme-error)', bgColor: 'rgba(239, 68, 68, 0.1)', icon: XCircle },
  warning: { color: 'var(--theme-warning)', bgColor: 'rgba(245, 158, 11, 0.1)', icon: AlertCircle },
  download: { color: 'var(--theme-primary)', bgColor: 'rgba(59, 130, 246, 0.1)', icon: Download },
  auth: { color: 'var(--theme-steam)', bgColor: 'rgba(102, 192, 244, 0.1)', icon: LogIn },
  progress: { color: 'var(--theme-primary)', bgColor: 'rgba(59, 130, 246, 0.1)', icon: Loader2 },
  command: { color: 'var(--theme-accent)', bgColor: 'rgba(168, 85, 247, 0.1)', icon: Terminal },
  info: { color: 'var(--theme-text-muted)', bgColor: 'rgba(148, 163, 184, 0.08)', icon: Info }
};

const LogIcon = memo(({ type }: { type: LogEntryType }) => {
  const style = typeStyles[type];
  const Icon = style.icon;
  const isSpinning = type === 'progress';

  return (
    <div
      className="flex items-center justify-center rounded-md flex-shrink-0"
      style={{
        width: '28px',
        height: '28px',
        backgroundColor: style.bgColor,
      }}
    >
      <Icon
        className={`h-3.5 w-3.5 ${isSpinning ? 'animate-spin' : ''}`}
        style={{ color: style.color }}
      />
    </div>
  );
});

LogIcon.displayName = 'LogIcon';

const formatTime = (date: Date): string => {
  return date.toLocaleTimeString('en-US', {
    hour12: false,
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  });
};

const LogEntryRow = memo(({ entry, isLast }: { entry: LogEntry; isLast: boolean }) => {
  const style = typeStyles[entry.type];

  return (
    <div
      className="group flex items-start gap-3 px-4 py-3 transition-all duration-150"
      style={{
        borderBottom: isLast ? 'none' : '1px solid var(--theme-border-secondary)',
        borderLeft: `3px solid ${style.color}`,
        minHeight: '60px',
        backgroundColor: 'transparent',
      }}
      onMouseEnter={(e) => {
        e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
      }}
      onMouseLeave={(e) => {
        e.currentTarget.style.backgroundColor = 'transparent';
      }}
      title={entry.details ? `${entry.message}\n${entry.details}` : undefined}
    >
      {/* Timestamp */}
      <span
        className="text-[11px] font-mono flex-shrink-0 tabular-nums pt-1 opacity-60 group-hover:opacity-100 transition-opacity"
        style={{ color: 'var(--theme-text-muted)', letterSpacing: '0.02em' }}
      >
        {formatTime(entry.timestamp)}
      </span>

      {/* Icon */}
      <LogIcon type={entry.type} />

      {/* Message */}
      <div className="flex-1 min-w-0 pt-0.5">
        <p
          className="text-[13px] leading-relaxed"
          style={{
            color: 'var(--theme-text-primary)',
            wordBreak: 'break-word'
          }}
        >
          {entry.message}
        </p>
        {entry.details && (
          <p
            className="text-xs mt-1 opacity-70"
            style={{ color: 'var(--theme-text-muted)' }}
          >
            {entry.details}
          </p>
        )}
      </div>
    </div>
  );
});

LogEntryRow.displayName = 'LogEntryRow';

const ENTRIES_PER_PAGE = 10;
const PAGINATION_HEIGHT = 52; // Fixed height for pagination footer

export function ActivityLog({ entries, maxHeight = '400px', className = '' }: ActivityLogProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const shouldAutoScroll = useRef(true);
  const [currentPage, setCurrentPage] = useState(1);

  // Calculate pagination
  const totalPages = Math.ceil(entries.length / ENTRIES_PER_PAGE);
  const startIndex = (currentPage - 1) * ENTRIES_PER_PAGE;
  const endIndex = startIndex + ENTRIES_PER_PAGE;
  const visibleEntries = entries.slice(startIndex, endIndex);

  // Calculate content area height
  const contentHeight = useMemo(() => {
    if (maxHeight === '100%') return '100%';
    // Parse maxHeight and subtract pagination height if needed
    const hasPagination = totalPages > 1;
    if (!hasPagination) return maxHeight;

    // If maxHeight is a calc() or percentage, handle appropriately
    if (maxHeight.includes('calc') || maxHeight.includes('%')) {
      return `calc(${maxHeight} - ${PAGINATION_HEIGHT}px)`;
    }
    // Parse pixel value
    const parsed = parseInt(maxHeight, 10);
    if (!isNaN(parsed)) {
      return `${parsed - PAGINATION_HEIGHT}px`;
    }
    return maxHeight;
  }, [maxHeight, totalPages]);

  // Auto-advance to last page when new entries are added
  useEffect(() => {
    const newTotalPages = Math.ceil(entries.length / ENTRIES_PER_PAGE);
    if (shouldAutoScroll.current && newTotalPages > 0) {
      setCurrentPage(newTotalPages);
    }
  }, [entries.length]);

  // Auto-scroll to bottom when new entries are added
  useEffect(() => {
    if (containerRef.current && shouldAutoScroll.current) {
      const scrollableElement = containerRef.current.querySelector('.overflow-y-auto');
      if (scrollableElement) {
        scrollableElement.scrollTop = scrollableElement.scrollHeight;
      }
    }
  }, [visibleEntries]);

  // Listen for scroll events to detect if user scrolled up
  useEffect(() => {
    const handleScroll = (e: Event) => {
      const target = e.target as HTMLElement;
      const { scrollTop, scrollHeight, clientHeight } = target;
      shouldAutoScroll.current = scrollHeight - scrollTop - clientHeight < 50;
    };

    const scrollableElement = containerRef.current?.querySelector('.overflow-y-auto');
    if (scrollableElement) {
      scrollableElement.addEventListener('scroll', handleScroll);
      return () => scrollableElement.removeEventListener('scroll', handleScroll);
    }
  }, []);

  const handlePageChange = (page: number) => {
    if (page < 1 || page > totalPages) return;
    setCurrentPage(page);
    shouldAutoScroll.current = page === totalPages;
  };

  const startItem = startIndex + 1;
  const endItem = Math.min(endIndex, entries.length);

  return (
    <div
      ref={containerRef}
      className={`${className} flex flex-col`}
      style={{
        backgroundColor: 'var(--theme-bg-tertiary)',
        borderRadius: '10px',
        border: '1px solid var(--theme-border-secondary)',
        height: maxHeight === '100%' ? '100%' : undefined,
        maxHeight: maxHeight !== '100%' ? maxHeight : undefined,
        overflow: 'hidden',
      }}
    >
      {entries.length === 0 ? (
        /* Empty State */
        <div
          className="flex flex-col items-center justify-center flex-1 py-16 px-6"
          style={{
            background: `
              radial-gradient(circle at 50% 0%, rgba(59, 130, 246, 0.03) 0%, transparent 50%),
              var(--theme-bg-tertiary)
            `
          }}
        >
          <div
            className="relative w-16 h-16 rounded-2xl flex items-center justify-center mb-4"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
              boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.05)'
            }}
          >
            <Activity
              className="h-7 w-7"
              style={{ color: 'var(--theme-text-muted)', opacity: 0.5 }}
            />
            {/* Subtle pulse ring */}
            <div
              className="absolute inset-0 rounded-2xl animate-ping"
              style={{
                backgroundColor: 'var(--theme-primary)',
                opacity: 0.05,
                animationDuration: '2s'
              }}
            />
          </div>
          <p
            className="text-sm font-medium mb-1"
            style={{ color: 'var(--theme-text-primary)' }}
          >
            Waiting for activity
          </p>
          <p
            className="text-xs text-center max-w-[200px]"
            style={{ color: 'var(--theme-text-muted)', opacity: 0.7 }}
          >
            Commands and status updates will appear here
          </p>
        </div>
      ) : (
        <>
          {/* Scrollable Log Entries */}
          <div className="flex-1 min-h-0">
            <CustomScrollbar
              maxHeight={contentHeight}
              paddingMode="none"
            >
              <div>
                {visibleEntries.map((entry, index) => (
                  <LogEntryRow
                    key={entry.id}
                    entry={entry}
                    isLast={index === visibleEntries.length - 1}
                  />
                ))}
              </div>
            </CustomScrollbar>
          </div>

          {/* Pagination Footer - Fixed Height */}
          {totalPages > 1 && (
            <div
              className="flex-shrink-0 flex items-center justify-between px-4"
              style={{
                height: `${PAGINATION_HEIGHT}px`,
                borderTop: '1px solid var(--theme-border-secondary)',
                backgroundColor: 'var(--theme-bg-secondary)',
              }}
            >
              {/* Entry count */}
              <span
                className="text-xs tabular-nums"
                style={{ color: 'var(--theme-text-muted)' }}
              >
                {startItem}–{endItem} of {entries.length}
              </span>

              {/* Navigation controls */}
              <div className="flex items-center gap-2">
                <button
                  onClick={() => handlePageChange(currentPage - 1)}
                  disabled={currentPage === 1}
                  className="flex items-center justify-center w-8 h-8 rounded-lg transition-all disabled:opacity-30 disabled:cursor-not-allowed"
                  style={{
                    backgroundColor: 'var(--theme-bg-tertiary)',
                    border: '1px solid var(--theme-border-secondary)',
                    color: 'var(--theme-text-primary)',
                  }}
                  onMouseEnter={(e) => {
                    if (!e.currentTarget.disabled) {
                      e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                      e.currentTarget.style.borderColor = 'var(--theme-border-primary)';
                    }
                  }}
                  onMouseLeave={(e) => {
                    e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)';
                    e.currentTarget.style.borderColor = 'var(--theme-border-secondary)';
                  }}
                  aria-label="Previous page"
                >
                  <ChevronLeft size={16} />
                </button>

                {/* Page indicator */}
                <span
                  className="text-xs font-medium tabular-nums px-2 min-w-[60px] text-center"
                  style={{ color: 'var(--theme-text-primary)' }}
                >
                  {currentPage} / {totalPages}
                </span>

                <button
                  onClick={() => handlePageChange(currentPage + 1)}
                  disabled={currentPage === totalPages}
                  className="flex items-center justify-center w-8 h-8 rounded-lg transition-all disabled:opacity-30 disabled:cursor-not-allowed"
                  style={{
                    backgroundColor: 'var(--theme-bg-tertiary)',
                    border: '1px solid var(--theme-border-secondary)',
                    color: 'var(--theme-text-primary)',
                  }}
                  onMouseEnter={(e) => {
                    if (!e.currentTarget.disabled) {
                      e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                      e.currentTarget.style.borderColor = 'var(--theme-border-primary)';
                    }
                  }}
                  onMouseLeave={(e) => {
                    e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)';
                    e.currentTarget.style.borderColor = 'var(--theme-border-secondary)';
                  }}
                  aria-label="Next page"
                >
                  <ChevronRight size={16} />
                </button>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}

// Helper to create log entries
export function createLogEntry(
  type: LogEntryType,
  message: string,
  details?: string
): LogEntry {
  return {
    id: `${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
    timestamp: new Date(),
    type,
    message,
    details
  };
}

export default ActivityLog;
