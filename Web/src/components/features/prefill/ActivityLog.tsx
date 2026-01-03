import { useEffect, useRef, useState, memo } from 'react';
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

// Type-to-style mapping using theme-aware color-mix for backgrounds
const typeStyles: Record<LogEntryType, { color: string; bgColor: string; icon: typeof Info }> = {
  success: { color: 'var(--theme-success)', bgColor: 'color-mix(in srgb, var(--theme-success) 12%, transparent)', icon: CheckCircle2 },
  error: { color: 'var(--theme-error)', bgColor: 'color-mix(in srgb, var(--theme-error) 12%, transparent)', icon: XCircle },
  warning: { color: 'var(--theme-warning)', bgColor: 'color-mix(in srgb, var(--theme-warning) 12%, transparent)', icon: AlertCircle },
  download: { color: 'var(--theme-primary)', bgColor: 'color-mix(in srgb, var(--theme-primary) 12%, transparent)', icon: Download },
  auth: { color: 'var(--theme-steam)', bgColor: 'color-mix(in srgb, var(--theme-steam) 12%, transparent)', icon: LogIn },
  progress: { color: 'var(--theme-primary)', bgColor: 'color-mix(in srgb, var(--theme-primary) 12%, transparent)', icon: Loader2 },
  command: { color: 'var(--theme-accent)', bgColor: 'color-mix(in srgb, var(--theme-accent) 12%, transparent)', icon: Terminal },
  info: { color: 'var(--theme-text-muted)', bgColor: 'color-mix(in srgb, var(--theme-text-muted) 8%, transparent)', icon: Info }
};

const LogIcon = memo(({ type }: { type: LogEntryType }) => {
  const style = typeStyles[type];
  const Icon = style.icon;
  const isSpinning = type === 'progress';

  return (
    <div
      className="flex items-center justify-center rounded-md flex-shrink-0"
      style={{
        width: '26px',
        height: '26px',
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
      className="group flex items-start gap-3 px-3 py-2.5 transition-colors duration-100"
      style={{
        borderBottom: isLast ? 'none' : '1px solid var(--theme-border-secondary)',
        backgroundColor: 'transparent',
      }}
      onMouseEnter={(e) => {
        e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
      }}
      onMouseLeave={(e) => {
        e.currentTarget.style.backgroundColor = 'transparent';
      }}
    >
      {/* Timestamp */}
      <span
        className="text-[11px] font-mono flex-shrink-0 tabular-nums pt-1.5 opacity-50 group-hover:opacity-80 transition-opacity"
        style={{ color: 'var(--theme-text-muted)', letterSpacing: '0.02em' }}
      >
        {formatTime(entry.timestamp)}
      </span>

      {/* Color indicator line */}
      <div
        className="w-0.5 self-stretch rounded-full flex-shrink-0 opacity-60"
        style={{ backgroundColor: style.color, minHeight: '20px' }}
      />

      {/* Icon */}
      <LogIcon type={entry.type} />

      {/* Message content */}
      <div className="flex-1 min-w-0 pt-0.5">
        <p
          className="text-[13px] leading-snug"
          style={{
            color: 'var(--theme-text-primary)',
            wordBreak: 'break-word'
          }}
        >
          {entry.message}
        </p>
        {entry.details && (
          <p
            className="text-xs mt-0.5 opacity-60"
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

export function ActivityLog({ entries, maxHeight = '400px', className = '' }: ActivityLogProps) {
  const shouldAutoScroll = useRef(true);
  const [currentPage, setCurrentPage] = useState(1);
  const scrollContainerRef = useRef<HTMLDivElement>(null);

  // Calculate pagination
  const totalPages = Math.ceil(entries.length / ENTRIES_PER_PAGE);
  const startIndex = (currentPage - 1) * ENTRIES_PER_PAGE;
  const endIndex = startIndex + ENTRIES_PER_PAGE;
  const visibleEntries = entries.slice(startIndex, endIndex);

  // Auto-advance to last page when new entries are added
  useEffect(() => {
    const newTotalPages = Math.ceil(entries.length / ENTRIES_PER_PAGE);
    if (shouldAutoScroll.current && newTotalPages > 0) {
      setCurrentPage(newTotalPages);
    }
  }, [entries.length]);

  // Auto-scroll to bottom when new entries are added on current page
  useEffect(() => {
    if (scrollContainerRef.current && shouldAutoScroll.current) {
      scrollContainerRef.current.scrollTop = scrollContainerRef.current.scrollHeight;
    }
  }, [visibleEntries]);

  // Listen for scroll events to detect if user scrolled up
  useEffect(() => {
    const scrollableElement = scrollContainerRef.current;
    if (!scrollableElement) return;

    const handleScroll = () => {
      const { scrollTop, scrollHeight, clientHeight } = scrollableElement;
      shouldAutoScroll.current = scrollHeight - scrollTop - clientHeight < 50;
    };

    scrollableElement.addEventListener('scroll', handleScroll);
    return () => scrollableElement.removeEventListener('scroll', handleScroll);
  }, []);

  const handlePageChange = (page: number) => {
    if (page < 1 || page > totalPages) return;
    setCurrentPage(page);
    shouldAutoScroll.current = page === totalPages;
    // Scroll to top of new page
    if (scrollContainerRef.current) {
      scrollContainerRef.current.scrollTop = 0;
    }
  };

  const startItem = startIndex + 1;
  const endItem = Math.min(endIndex, entries.length);

  return (
    <div
      className={`${className} flex flex-col`}
      style={{
        backgroundColor: 'var(--theme-bg-tertiary)',
        borderRadius: 'var(--theme-border-radius-lg, 0.75rem)',
        border: '1px solid var(--theme-border-secondary)',
        height: maxHeight,
        maxHeight: maxHeight,
        overflow: 'hidden',
      }}
    >
      {entries.length === 0 ? (
        /* Empty State */
        <div className="flex flex-col items-center justify-center flex-1 py-12 px-6">
          <div
            className="relative w-14 h-14 rounded-xl flex items-center justify-center mb-3"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
            }}
          >
            <Activity
              className="h-6 w-6"
              style={{ color: 'var(--theme-text-muted)', opacity: 0.4 }}
            />
          </div>
          <p
            className="text-sm font-medium mb-0.5"
            style={{ color: 'var(--theme-text-primary)' }}
          >
            Waiting for activity
          </p>
          <p
            className="text-xs text-center max-w-[180px]"
            style={{ color: 'var(--theme-text-muted)', opacity: 0.6 }}
          >
            Commands and status updates will appear here
          </p>
        </div>
      ) : (
        <>
          {/* Scrollable Log Entries Area - min-h-0 is crucial for flex overflow */}
          <div
            ref={scrollContainerRef}
            className="flex-1 overflow-y-auto overflow-x-hidden min-h-0"
            style={{
              scrollbarWidth: 'thin',
              scrollbarColor: 'var(--theme-scrollbar-thumb) var(--theme-scrollbar-track)',
            }}
          >
            <div className="py-1">
              {visibleEntries.map((entry, index) => (
                <LogEntryRow
                  key={entry.id}
                  entry={entry}
                  isLast={index === visibleEntries.length - 1}
                />
              ))}
            </div>
          </div>

          {/* Fixed Pagination Footer - flex-shrink-0 keeps it always visible */}
          {totalPages > 1 && (
            <div
              className="flex-shrink-0 flex items-center justify-between px-3"
              style={{
                height: '44px',
                minHeight: '44px',
                borderTop: '1px solid var(--theme-border-secondary)',
                backgroundColor: 'var(--theme-bg-secondary)',
              }}
            >
              {/* Entry count */}
              <span
                className="text-[11px] tabular-nums"
                style={{ color: 'var(--theme-text-muted)' }}
              >
                {startItem}–{endItem} of {entries.length}
              </span>

              {/* Navigation controls */}
              <div className="flex items-center gap-1.5">
                <button
                  onClick={() => handlePageChange(currentPage - 1)}
                  disabled={currentPage === 1}
                  className="flex items-center justify-center w-7 h-7 rounded-md transition-all disabled:opacity-30 disabled:cursor-not-allowed"
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
                  <ChevronLeft size={14} />
                </button>

                {/* Page indicator */}
                <span
                  className="text-[11px] font-medium tabular-nums px-1.5 min-w-[48px] text-center"
                  style={{ color: 'var(--theme-text-primary)' }}
                >
                  {currentPage} / {totalPages}
                </span>

                <button
                  onClick={() => handlePageChange(currentPage + 1)}
                  disabled={currentPage === totalPages}
                  className="flex items-center justify-center w-7 h-7 rounded-md transition-all disabled:opacity-30 disabled:cursor-not-allowed"
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
                  <ChevronRight size={14} />
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
