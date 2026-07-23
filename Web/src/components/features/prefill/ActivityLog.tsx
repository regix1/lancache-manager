import { useEffect, useRef, useState, memo } from 'react';
import { useTranslation } from 'react-i18next';
import { usePaginatedList } from '@/hooks/usePaginatedList';
import { EmptyState } from '@components/ui/ManagerCard';
import {
  CheckCircle2,
  AlertCircle,
  Info,
  Download,
  LogIn,
  Loader2 as Loader2Icon,
  XCircle,
  Terminal,
  ChevronLeft,
  ChevronRight,
  Activity
} from 'lucide-react';

import type { LogEntryType, LogEntry } from './ActivityLog.utils';

interface ActivityLogProps {
  entries: LogEntry[];
  className?: string;
  /** Service whose accent colors the auth-log rows (defaults to Steam). */
  serviceId?: string;
  /** When rendered inside a parent Card that already frames it, drops this component's own
      surface (background, border, radius) so the log isn't double-painted. */
  nested?: boolean;
}

// Per-service accent variables for the auth log row, set on the ActivityLog root so the Xbox
// device-code (and Epic/Riot/BN) auth entries aren't tinted Steam-blue. Unknown ids fall back
// to Steam. The `auth` typeStyle reads these vars (with a Steam default) so the memoized
// module-level LogIcon stays untouched.
const AUTH_ACCENT_VARS: Record<string, { color: string; bgColor: string }> = {
  steam: { color: 'var(--theme-steam)', bgColor: 'var(--theme-steam-subtle)' },
  epic: { color: 'var(--theme-epic)', bgColor: 'var(--theme-epic-subtle)' },
  battlenet: { color: 'var(--theme-blizzard)', bgColor: 'var(--theme-blizzard-subtle)' },
  riot: { color: 'var(--theme-riot)', bgColor: 'var(--theme-riot-subtle)' },
  xbox: { color: 'var(--theme-xbox)', bgColor: 'var(--theme-xbox-subtle)' }
};

// Type-to-style mapping using theme-aware CSS custom properties for backgrounds
const typeStyles: Record<LogEntryType, { color: string; bgColor: string; icon: typeof Info }> = {
  success: {
    color: 'var(--theme-success)',
    bgColor: 'var(--theme-success-subtle)',
    icon: CheckCircle2
  },
  error: {
    color: 'var(--theme-error)',
    bgColor: 'var(--theme-error-subtle)',
    icon: XCircle
  },
  warning: {
    color: 'var(--theme-warning)',
    bgColor: 'var(--theme-warning-subtle)',
    icon: AlertCircle
  },
  download: {
    color: 'var(--theme-primary)',
    bgColor: 'var(--theme-primary-subtle)',
    icon: Download
  },
  auth: {
    color: 'var(--prefill-auth-accent, var(--theme-steam))',
    bgColor: 'var(--prefill-auth-accent-bg, var(--theme-steam-subtle))',
    icon: LogIn
  },
  progress: {
    color: 'var(--theme-primary)',
    bgColor: 'var(--theme-primary-subtle)',
    icon: Loader2Icon
  },
  command: {
    color: 'var(--theme-accent)',
    bgColor: 'var(--theme-accent-subtle)',
    icon: Terminal
  },
  info: {
    color: 'var(--theme-text-muted)',
    bgColor: 'var(--theme-text-muted-faint)',
    icon: Info
  }
};

const LogIcon = memo(({ type }: { type: LogEntryType }) => {
  const style = typeStyles[type];
  const Icon = style.icon;
  const isSpinning = type === 'progress';

  return (
    <div
      className="flex items-center justify-center rounded-md flex-shrink-0 w-[26px] h-[26px] prefill-log-icon-bg"
      style={{ '--log-icon-bg': style.bgColor } as React.CSSProperties}
    >
      <Icon
        className={`h-3.5 w-3.5 prefill-log-icon ${isSpinning ? 'animate-spin' : ''}`}
        style={{ '--log-icon-color': style.color } as React.CSSProperties}
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

const LogEntryRow = memo(
  ({ entry, isLast, locale }: { entry: LogEntry; isLast: boolean; locale: string }) => {
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
          className="w-0.5 self-stretch rounded-full flex-shrink-0 opacity-60 min-h-[20px] prefill-log-color-line"
          style={{ '--log-line-color': style.color } as React.CSSProperties}
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
  }
);

LogEntryRow.displayName = 'LogEntryRow';

const ENTRIES_PER_PAGE = 10;

export function ActivityLog({
  entries,
  className = '',
  serviceId = 'steam',
  nested = false
}: ActivityLogProps) {
  const { t, i18n } = useTranslation();
  const shouldAutoScroll = useRef(true);
  const [currentPage, setCurrentPage] = useState(1);
  const locale = i18n.language || 'en-US';
  const authAccent = AUTH_ACCENT_VARS[serviceId] ?? AUTH_ACCENT_VARS.steam;

  // Controlled pagination via shared hook; hook handles clamping and slice math.
  const { totalPages, paginatedItems: visibleEntries } = usePaginatedList<LogEntry>({
    items: entries,
    pageSize: ENTRIES_PER_PAGE,
    page: currentPage,
    onPageChange: setCurrentPage
  });

  // Auto-advance to last page when new entries are added.
  // Hook clamps setPage(totalPages) to a safe range automatically.
  useEffect(() => {
    if (shouldAutoScroll.current && totalPages > 0) {
      setCurrentPage(totalPages);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entries.length]);

  const handlePageChange = (page: number) => {
    if (page < 1 || page > totalPages) return;
    setCurrentPage(page);
    shouldAutoScroll.current = page === totalPages;
  };

  // Page position for the count label, computed from hook outputs (no slice math).
  const endItem = Math.min(currentPage * ENTRIES_PER_PAGE, entries.length);
  const startItem = entries.length === 0 ? 0 : endItem - visibleEntries.length + 1;

  const surfaceClass = nested
    ? ''
    : 'bg-[var(--theme-bg-tertiary)] rounded-xl border border-[var(--theme-border-secondary)]';

  return (
    <div
      className={`${className} ${surfaceClass} overflow-hidden`}
      style={
        {
          '--prefill-auth-accent': authAccent.color,
          '--prefill-auth-accent-bg': authAccent.bgColor
        } as React.CSSProperties
      }
    >
      {entries.length === 0 ? (
        <div className="py-4 px-6">
          <EmptyState
            icon={Activity}
            title={t('prefill.activityLog.waitingForActivity')}
            subtitle={t('prefill.activityLog.updatesWillAppear')}
          />
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
            <div className="flex items-center justify-between px-2 sm:px-3 h-12 min-h-[48px] border-t border-[var(--theme-border-secondary)] bg-[var(--theme-bg-secondary)] rounded-b-xl">
              {/* Entry count - hidden on very small screens */}
              <span className="hidden sm:block text-[10px] sm:text-[11px] tabular-nums text-[var(--theme-text-muted)]">
                {t('prefill.activityLog.paginationCount', {
                  start: startItem,
                  end: endItem,
                  total: entries.length
                })}
              </span>

              {/* Navigation controls - centered on very small screens */}
              <div className="flex items-center gap-1 sm:gap-1.5 mx-auto sm:mx-0">
                <button
                  onClick={() => handlePageChange(currentPage - 1)}
                  disabled={currentPage === 1}
                  className="flex items-center justify-center min-w-[44px] min-h-[44px] w-11 h-11 rounded-lg transition-[background-color,border-color] duration-150 disabled:opacity-30 disabled:cursor-not-allowed bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)] text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-hover)] hover:border-[var(--theme-border-primary)] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--theme-border-focus)]"
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
                  className="flex items-center justify-center min-w-[44px] min-h-[44px] w-11 h-11 rounded-lg transition-[background-color,border-color] duration-150 disabled:opacity-30 disabled:cursor-not-allowed bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-secondary)] text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-hover)] hover:border-[var(--theme-border-primary)] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--theme-border-focus)]"
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
