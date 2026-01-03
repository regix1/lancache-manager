import { useEffect, useRef, useState, memo } from 'react';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { Pagination } from '@components/ui/Pagination';
import {
  CheckCircle2,
  AlertCircle,
  Info,
  Download,
  LogIn,
  Loader2,
  Clock,
  XCircle,
  Terminal
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

const LogIcon = memo(({ type }: { type: LogEntryType }) => {
  const iconClass = "h-4 w-4 flex-shrink-0";

  switch (type) {
    case 'success':
      return <CheckCircle2 className={iconClass} style={{ color: 'var(--theme-success)' }} />;
    case 'error':
      return <XCircle className={iconClass} style={{ color: 'var(--theme-error)' }} />;
    case 'warning':
      return <AlertCircle className={iconClass} style={{ color: 'var(--theme-warning)' }} />;
    case 'download':
      return <Download className={iconClass} style={{ color: 'var(--theme-primary)' }} />;
    case 'auth':
      return <LogIn className={iconClass} style={{ color: 'var(--theme-steam)' }} />;
    case 'progress':
      return <Loader2 className={`${iconClass} animate-spin`} style={{ color: 'var(--theme-primary)' }} />;
    case 'command':
      return <Terminal className={iconClass} style={{ color: 'var(--theme-accent)' }} />;
    default:
      return <Info className={iconClass} style={{ color: 'var(--theme-text-muted)' }} />;
  }
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

const LogEntryRow = memo(({ entry }: { entry: LogEntry }) => (
  <div
    className="flex items-center gap-3 py-2 px-4 transition-colors"
    style={{
      borderBottom: '1px solid var(--theme-border-secondary)',
      minHeight: '40px'
    }}
    onMouseEnter={(e) => {
      e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
    }}
    onMouseLeave={(e) => {
      e.currentTarget.style.backgroundColor = 'transparent';
    }}
    title={entry.details ? `${entry.message}\n${entry.details}` : entry.message}
  >
    <span
      className="text-xs font-mono flex-shrink-0 tabular-nums"
      style={{ color: 'var(--theme-text-muted)' }}
    >
      {formatTime(entry.timestamp)}
    </span>
    <LogIcon type={entry.type} />
    <div className="flex-1 min-w-0">
      <span
        className="text-sm truncate block"
        style={{ color: 'var(--theme-text-primary)' }}
      >
        {entry.message}
      </span>
    </div>
  </div>
));

LogEntryRow.displayName = 'LogEntryRow';

const ENTRIES_PER_PAGE = 10;

export function ActivityLog({ entries, maxHeight = '400px', className = '' }: ActivityLogProps) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const shouldAutoScroll = useRef(true);
  const [currentPage, setCurrentPage] = useState(1);

  // Calculate pagination
  const totalPages = Math.ceil(entries.length / ENTRIES_PER_PAGE);
  const startIndex = (currentPage - 1) * ENTRIES_PER_PAGE;
  const endIndex = startIndex + ENTRIES_PER_PAGE;
  const visibleEntries = entries.slice(startIndex, endIndex);

  // Auto-advance to last page when new entries are added (if on last page)
  useEffect(() => {
    const newTotalPages = Math.ceil(entries.length / ENTRIES_PER_PAGE);
    if (shouldAutoScroll.current && newTotalPages > 0) {
      setCurrentPage(newTotalPages);
    }
  }, [entries.length]);

  // Auto-scroll to bottom when new entries are added
  useEffect(() => {
    if (scrollRef.current && shouldAutoScroll.current) {
      // Find the scrollable element within CustomScrollbar
      const scrollableElement = scrollRef.current.querySelector('.overflow-y-auto');
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
      // If scrolled within 50px of bottom, enable auto-scroll
      shouldAutoScroll.current = scrollHeight - scrollTop - clientHeight < 50;
    };

    const scrollableElement = scrollRef.current?.querySelector('.overflow-y-auto');
    if (scrollableElement) {
      scrollableElement.addEventListener('scroll', handleScroll);
      return () => scrollableElement.removeEventListener('scroll', handleScroll);
    }
  }, []);

  const handlePageChange = (page: number) => {
    setCurrentPage(page);
    // Re-enable auto-scroll if going to last page, disable otherwise
    shouldAutoScroll.current = page === totalPages;
  };

  return (
    <div
      ref={scrollRef}
      className={`${className} flex flex-col`}
      style={{
        backgroundColor: 'var(--theme-bg-tertiary)',
        borderRadius: 'var(--theme-border-radius-lg, 0.5rem)',
        border: '1px solid var(--theme-border-secondary)',
        height: maxHeight === '100%' ? '100%' : undefined,
        maxHeight: maxHeight !== '100%' ? maxHeight : undefined,
        overflow: 'hidden'
      }}
    >
      {entries.length === 0 ? (
        <div
          className="flex flex-col items-center justify-center py-12 flex-1"
          style={{ color: 'var(--theme-text-muted)' }}
        >
          <div
            className="w-12 h-12 rounded-xl flex items-center justify-center mb-3"
            style={{ backgroundColor: 'var(--theme-bg-secondary)' }}
          >
            <Clock className="h-6 w-6 opacity-50" />
          </div>
          <p className="text-sm font-medium">Waiting for activity...</p>
          <p className="text-xs mt-1 opacity-70">Commands and status updates will appear here</p>
        </div>
      ) : (
        <>
          <CustomScrollbar maxHeight={totalPages > 1 ? `calc(${maxHeight} - 56px)` : maxHeight} paddingMode="compact" className="flex-1 min-h-0">
            <div>
              {visibleEntries.map((entry) => (
                <LogEntryRow key={entry.id} entry={entry} />
              ))}
            </div>
          </CustomScrollbar>

          {/* Pagination Controls - only show when multiple pages */}
          {totalPages > 1 && (
            <div
              className="flex-shrink-0 px-4 py-3"
              style={{
                borderTop: '1px solid var(--theme-border-secondary)',
                backgroundColor: 'var(--theme-bg-secondary)'
              }}
            >
              <Pagination
                currentPage={currentPage}
                totalPages={totalPages}
                totalItems={entries.length}
                itemsPerPage={ENTRIES_PER_PAGE}
                onPageChange={handlePageChange}
                itemLabel="entries"
                showCard={false}
                parentPadding="none"
                compact
              />
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
