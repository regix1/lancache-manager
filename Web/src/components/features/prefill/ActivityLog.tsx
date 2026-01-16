import { useEffect, useRef, useState, memo } from 'react';
import { useTranslation } from 'react-i18next';
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
      className="flex items-center justify-center rounded-md flex-shrink-0 w-[26px] h-[26px]"
      style={{ backgroundColor: style.bgColor }}
    >
      <Icon
        className={`h-3.5 w-3.5 ${isSpinning ? 'animate-spin' : ''}`}
        style={{ color: style.color }}
      />
    </div>
  );
});

LogIcon.displayName = 'LogIcon';

const formatTime = (date: Date, locale: string): string => {
  return date.toLocaleTimeString(locale || 'en-US', {
    hour12: false,
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  });
};

const LogEntryRow = memo(({ entry, isLast, locale }: { entry: LogEntry; isLast: boolean; locale: string }) => {
  const style = typeStyles[entry.type];

  return (
    <div
      className={`group flex items-start gap-2 sm:gap-3 px-2 sm:px-3 py-2 sm:py-2.5 transition-colors duration-100 hover:bg-[var(--theme-bg-hover)] ${
        isLast ? '' : 'border-b border-[var(--theme-border-secondary)]'
      }`}
    >
      {/* Timestamp - hidden on mobile, shown on sm+ */}
      <span className="hidden sm:block text-[11px] font-mono flex-shrink-0 tabular-nums pt-1.5 opacity-50 group-hover:opacity-80 transition-opacity text-[var(--theme-text-muted)] tracking-[0.02em]">
        {formatTime(entry.timestamp, locale)}
      </span>

      {/* Color indicator line */}
      <div
        className="w-0.5 self-stretch rounded-full flex-shrink-0 opacity-60 min-h-[20px]"
        style={{ backgroundColor: style.color }}
      />

      {/* Icon */}
      <LogIcon type={entry.type} />

      {/* Message content */}
      <div className="flex-1 min-w-0 pt-0.5">
        <p className="text-xs sm:text-[13px] leading-snug text-[var(--theme-text-primary)] break-words">
          {entry.message}
        </p>
        {entry.details && (
          <p className="text-[11px] sm:text-xs mt-0.5 opacity-60 text-[var(--theme-text-muted)]">
            {entry.details}
          </p>
        )}
      </div>
    </div>
  );
});

LogEntryRow.displayName = 'LogEntryRow';

const ENTRIES_PER_PAGE = 10;

export function ActivityLog({ entries, className = '' }: ActivityLogProps) {
  const { t, i18n } = useTranslation();
  const shouldAutoScroll = useRef(true);
  const [currentPage, setCurrentPage] = useState(1);
  const locale = i18n.language || 'en-US';

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

  const handlePageChange = (page: number) => {
    if (page < 1 || page > totalPages) return;
    setCurrentPage(page);
    shouldAutoScroll.current = page === totalPages;
  };

  const startItem = startIndex + 1;
  const endItem = Math.min(endIndex, entries.length);

  return (
    <div
      className={`${className} bg-[var(--theme-bg-tertiary)] rounded-xl border border-[var(--theme-border-secondary)] overflow-hidden`}
    >
      {entries.length === 0 ? (
        /* Empty State */
        <div className="flex flex-col items-center justify-center py-12 px-6">
          <div className="relative w-14 h-14 rounded-xl flex items-center justify-center mb-3 bg-[var(--theme-bg-secondary)]">
            <Activity className="h-6 w-6 text-[var(--theme-text-muted)] opacity-40" />
          </div>
          <p className="text-sm font-medium mb-0.5 text-[var(--theme-text-primary)]">
            {t('prefill.activityLog.waitingForActivity')}
          </p>
          <p className="text-xs text-center max-w-[180px] text-[var(--theme-text-muted)] opacity-60">
            {t('prefill.activityLog.updatesWillAppear')}
          </p>
        </div>
      ) : (
        <>
          {/* Log Entries - max 10 per page */}
          <div className={`pt-1 ${totalPages <= 1 ? 'pb-2' : 'pb-1'}`}>
            {visibleEntries.map((entry, index) => (
              <LogEntryRow
                key={entry.id}
                entry={entry}
                isLast={index === visibleEntries.length - 1}
                locale={locale}
              />
            ))}
          </div>

          {/* Pagination Footer */}
          {totalPages > 1 && (
            <div
              className="flex items-center justify-between px-2 sm:px-3 h-12 min-h-[48px] border-t border-[var(--theme-border-secondary)] bg-[var(--theme-bg-secondary)] rounded-b-xl"
            >
              {/* Entry count - hidden on very small screens */}
              <span className="hidden xs:block text-[10px] sm:text-[11px] tabular-nums text-[var(--theme-text-muted)]">
                {t('prefill.activityLog.paginationCount', {
                  start: startItem,
                  end: endItem,
                  total: entries.length
                })}
              </span>

              {/* Navigation controls - centered on very small screens */}
              <div className="flex items-center gap-1 sm:gap-1.5 mx-auto xs:mx-0">
                <button
                  onClick={() => handlePageChange(currentPage - 1)}
                  disabled={currentPage === 1}
                  className="flex items-center justify-center w-8 h-8 sm:w-7 sm:h-7 rounded-md transition-all disabled:opacity-30 disabled:cursor-not-allowed bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)] text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-hover)] hover:border-[var(--theme-border-primary)]"
                  aria-label={t('aria.previousPage')}
                >
                  <ChevronLeft size={14} />
                </button>

                {/* Page indicator */}
                <span className="text-[11px] font-medium tabular-nums px-1.5 min-w-[48px] text-center text-[var(--theme-text-primary)]">
                  {currentPage} / {totalPages}
                </span>

                <button
                  onClick={() => handlePageChange(currentPage + 1)}
                  disabled={currentPage === totalPages}
                  className="flex items-center justify-center w-8 h-8 sm:w-7 sm:h-7 rounded-md transition-all disabled:opacity-30 disabled:cursor-not-allowed bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)] text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-hover)] hover:border-[var(--theme-border-primary)]"
                  aria-label={t('aria.nextPage')}
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
