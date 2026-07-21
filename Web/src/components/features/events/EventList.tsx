import React, { useMemo, useState, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Calendar,
  Clock,
  ChevronRight,
  Zap,
  History,
  CalendarClock,
  Pencil,
  BarChart3,
  MoreVertical
} from 'lucide-react';
import { useTimezone } from '@contexts/useTimezone';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { ActionMenu, ActionMenuItem } from '@components/ui/ActionMenu';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatBytes } from '@utils/formatters';
import { getEventColorStyles, getEventColorVar } from '@utils/eventColors';
import ApiService from '@services/api.service';
import { useErrorHandler } from '@hooks/useErrorHandler';
import type { Event, Download } from '../../../types';
import { APP_EVENTS } from '@utils/constants';

type EventDownloadsCache = Record<
  number,
  {
    downloads: Download[];
    loading: boolean;
    loaded: boolean;
  }
>;

// Group downloads by game name - moved outside component
const groupDownloadsByGame = (downloads: Download[], unknownLabel: string) => {
  const grouped: Record<
    string,
    { name: string; service: string; totalBytes: number; count: number }
  > = {};

  downloads.forEach((d) => {
    const key = `${d.service}-${d.gameName || unknownLabel}`;
    if (!grouped[key]) {
      grouped[key] = {
        name: d.gameName || unknownLabel,
        service: d.service,
        totalBytes: 0,
        count: 0
      };
    }
    grouped[key].totalBytes += d.totalBytes || 0;
    grouped[key].count += 1;
  });

  return Object.values(grouped).sort((a, b) => b.totalBytes - a.totalBytes);
};

// EventCard defined OUTSIDE of EventList to prevent recreation on each render
interface EventCardProps {
  event: Event;
  status: 'active' | 'upcoming' | 'past';
  isExpanded: boolean;
  cacheEntry: EventDownloadsCache[number] | undefined;
  onExpandClick: (eventId: number) => void;
  onEditClick: (event: Event) => void;
  onViewStatsClick: (event: Event) => void;
  formatDateTime: (dateStr: string) => string;
  formatDurationBetweenDates: (startStr: string, endStr: string) => string;
}

const EventCard = React.memo(
  ({
    event,
    status,
    isExpanded,
    cacheEntry,
    onExpandClick,
    onEditClick,
    onViewStatsClick,
    formatDateTime,
    formatDurationBetweenDates
  }: EventCardProps) => {
    const { t } = useTranslation();
    const [menuOpen, setMenuOpen] = useState(false);
    const isLoading = isExpanded && (cacheEntry?.loading || false);
    const groupedDownloads = useMemo(() => {
      const downloads = cacheEntry?.downloads || [];
      if (!isExpanded || downloads.length === 0) {
        return [];
      }
      return groupDownloadsByGame(downloads, t('events.list.unknownGame'));
    }, [cacheEntry?.downloads, isExpanded, t]);
    const formattedStart = useMemo(
      () => formatDateTime(event.startTimeUtc),
      [formatDateTime, event.startTimeUtc]
    );
    const formattedDuration = useMemo(
      () => formatDurationBetweenDates(event.startTimeUtc, event.endTimeUtc),
      [formatDurationBetweenDates, event.startTimeUtc, event.endTimeUtc]
    );

    const colorVar = getEventColorVar(event.colorIndex);

    return (
      <div
        className="rounded-lg border overflow-hidden cursor-pointer"
        style={{
          backgroundColor: 'var(--theme-bg-secondary)',
          borderColor: isExpanded
            ? colorVar
            : status === 'active'
              ? colorVar
              : 'var(--theme-border-primary)',
          borderWidth: status === 'active' || isExpanded ? '2px' : '1px',
          opacity: status === 'past' ? 0.65 : 1
        }}
        onClick={() => onExpandClick(event.id)}
      >
        {/* Active indicator bar */}
        {status === 'active' && (
          <div
            className="h-1"
            style={{
              background: `linear-gradient(90deg, ${colorVar}, ${colorVar.replace(')', '-intense)')})`
            }}
          />
        )}

        <div className="p-4">
          <div className="flex items-start justify-between gap-4">
            <div className="flex-1 min-w-0">
              {/* Header row with badge and name */}
              <div className="flex items-center gap-2.5 mb-2">
                {/* Expand arrow */}
                <ChevronRight
                  className={`w-4 h-4 flex-shrink-0 text-[var(--theme-text-secondary)] ${isExpanded ? 'rotate-90' : ''}`}
                />

                {/* Event color badge - pill style */}
                <span
                  className="inline-flex items-center gap-1.5 px-2 py-0.5 text-[10px] font-semibold rounded-full uppercase tracking-wide"
                  style={getEventColorStyles(event.colorIndex)}
                >
                  {status === 'active'
                    ? t('events.list.status.live')
                    : status === 'upcoming'
                      ? t('events.list.status.upcoming')
                      : t('events.list.status.ended')}
                </span>

                <h3 className="font-semibold truncate text-[var(--theme-text-primary)]">
                  {event.name}
                </h3>
              </div>

              {/* Description */}
              {event.description && (
                <p className="text-sm mb-3 line-clamp-2 ml-6 text-[var(--theme-text-secondary)]">
                  {event.description}
                </p>
              )}

              {/* Meta info row */}
              <div className="flex flex-wrap items-center gap-x-4 gap-y-1 ml-6">
                <div className="flex items-center gap-1.5 text-xs text-[var(--theme-text-muted)]">
                  <Calendar className="w-3.5 h-3.5" />
                  <span>{formattedStart}</span>
                </div>
                <div className="flex items-center gap-1.5 text-xs text-[var(--theme-text-muted)]">
                  <Clock className="w-3.5 h-3.5" />
                  <span>{formattedDuration}</span>
                </div>
              </div>
            </div>

            {/* Row actions collapsed into a single kebab menu instead of two
                always-visible icon buttons, so the row reads calm. */}
            <div className="flex-shrink-0" onClick={(e) => e.stopPropagation()}>
              <ActionMenu
                isOpen={menuOpen}
                onClose={() => setMenuOpen(false)}
                align="right"
                trigger={
                  <button
                    type="button"
                    onClick={(e) => {
                      e.stopPropagation();
                      setMenuOpen((prev) => !prev);
                    }}
                    className="btn-icon-square btn-icon-square--sm flex items-center hover:bg-[var(--theme-bg-hover)] bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)]"
                    aria-label={t('events.list.actionsMenuLabel', 'Event actions')}
                  >
                    <MoreVertical className="w-4 h-4" />
                  </button>
                }
              >
                <ActionMenuItem
                  onClick={() => {
                    setMenuOpen(false);
                    onViewStatsClick(event);
                  }}
                  icon={<BarChart3 className="w-4 h-4" />}
                >
                  {t('events.list.tooltips.viewStats')}
                </ActionMenuItem>
                <ActionMenuItem
                  onClick={() => {
                    setMenuOpen(false);
                    onEditClick(event);
                  }}
                  icon={<Pencil className="w-4 h-4" />}
                >
                  {t('events.list.tooltips.edit')}
                </ActionMenuItem>
              </ActionMenu>
            </div>
          </div>
        </div>

        {/* Expanded downloads section */}
        <CollapsibleRegion
          open={isExpanded}
          contentClassName="border-t border-[var(--theme-border-secondary)] px-4 py-3 bg-[var(--theme-bg-tertiary)]"
        >
          {isLoading ? (
            <div className="flex items-center justify-center py-4 gap-2">
              <LoadingSpinner inline size="sm" className="text-[var(--theme-primary)]" />
              <span className="text-sm text-[var(--theme-text-secondary)]">
                {t('events.list.loadingDownloads')}
              </span>
            </div>
          ) : groupedDownloads.length === 0 ? (
            <p className="text-sm text-center py-4 text-[var(--theme-text-muted)]">
              {t('events.list.emptyDownloads')}
            </p>
          ) : (
            <div className="space-y-2">
              <div className="flex items-center justify-between mb-2">
                <span className="text-xs font-medium text-[var(--theme-text-secondary)]">
                  {t('events.list.gamesDuringEvent')}
                </span>
                <span className="themed-badge status-badge-neutral badge-count">
                  {t('events.list.gameCount', { count: groupedDownloads.length })}
                </span>
              </div>
              {groupedDownloads.slice(0, 10).map((game, idx) => (
                <div
                  key={idx}
                  className="flex items-center justify-between py-2 px-3 rounded-lg bg-[var(--theme-bg-secondary)]"
                >
                  <div className="flex items-center gap-2 min-w-0">
                    <span className="themed-badge status-badge-neutral">
                      {game.service.toUpperCase()}
                    </span>
                    <span className="text-sm font-medium truncate text-[var(--theme-text-primary)]">
                      {game.name}
                    </span>
                  </div>
                  <div className="flex items-center gap-3 flex-shrink-0">
                    <span className="text-xs text-[var(--theme-text-muted)]">{game.count}x</span>
                    <span className="text-sm font-semibold text-[var(--theme-text-primary)]">
                      {formatBytes(game.totalBytes)}
                    </span>
                  </div>
                </div>
              ))}
              {groupedDownloads.length > 10 && (
                <p className="text-xs text-center pt-2 text-[var(--theme-text-muted)]">
                  {t('events.list.moreGames', { count: groupedDownloads.length - 10 })}
                </p>
              )}
            </div>
          )}
        </CollapsibleRegion>
      </div>
    );
  }
);

EventCard.displayName = 'EventCard';

interface EventListProps {
  events: Event[];
  onEventClick: (event: Event) => void;
}

const EventList: React.FC<EventListProps> = ({ events, onEventClick }) => {
  const { t } = useTranslation();
  const { use24HourFormat } = useTimezone();
  const { setTimeRange, setSelectedEventIds } = useTimeFilter();
  const { notifyError } = useErrorHandler();
  const [expandedEventId, setExpandedEventId] = useState<number | null>(null);
  const [downloadsCache, setDownloadsCache] = useState<EventDownloadsCache>({});
  const fetchingRef = useRef<Set<number>>(new Set());

  // Group events by status: active, upcoming, past
  const groupedEvents = useMemo(() => {
    const now = new Date();
    const active: Event[] = [];
    const upcoming: Event[] = [];
    const past: Event[] = [];

    events.forEach((event) => {
      const start = new Date(event.startTimeUtc);
      const end = new Date(event.endTimeUtc);

      if (now >= start && now <= end) {
        active.push(event);
      } else if (now < start) {
        upcoming.push(event);
      } else {
        past.push(event);
      }
    });

    // Sort upcoming by start date (soonest first)
    upcoming.sort(
      (a, b) => new Date(a.startTimeUtc).getTime() - new Date(b.startTimeUtc).getTime()
    );
    // Sort past by end date (most recent first)
    past.sort((a, b) => new Date(b.endTimeUtc).getTime() - new Date(a.endTimeUtc).getTime());

    return { active, upcoming, past };
  }, [events]);

  const formatDateTime = useCallback(
    (dateStr: string) => {
      const date = new Date(dateStr);
      return date.toLocaleString(undefined, {
        weekday: 'short',
        month: 'short',
        day: 'numeric',
        hour: 'numeric',
        minute: '2-digit',
        hour12: !use24HourFormat
      });
    },
    [use24HourFormat]
  );

  const formatDurationBetweenDates = useCallback(
    (startStr: string, endStr: string) => {
      const start = new Date(startStr);
      const end = new Date(endStr);
      const diffMs = end.getTime() - start.getTime();
      const hours = Math.floor(diffMs / (1000 * 60 * 60));
      const days = Math.floor(hours / 24);
      const remainingHours = hours % 24;

      if (days > 0) {
        if (remainingHours > 0) {
          return t('events.list.duration.daysHours', { days, hours: remainingHours });
        }
        return t('events.list.duration.days', { count: days });
      }
      return t('events.list.duration.hours', { count: hours });
    },
    [t]
  );

  const fetchEventDownloads = useCallback(
    async (eventId: number) => {
      // Already fetching or fetched
      if (fetchingRef.current.has(eventId)) {
        return;
      }
      fetchingRef.current.add(eventId);

      setDownloadsCache((prev) => ({
        ...prev,
        [eventId]: { downloads: [], loading: true, loaded: false }
      }));

      try {
        // Use taggedOnly=true to show only downloads explicitly tagged to this event
        const downloads = await ApiService.getEventDownloads<Download[]>(eventId);
        setDownloadsCache((prev) => ({
          ...prev,
          [eventId]: {
            downloads: Array.isArray(downloads) ? downloads : [],
            loading: false,
            loaded: true
          }
        }));
      } catch (error) {
        notifyError(t('events.list.errors.loadDownloadsFailed'), error, {
          logLabel: 'Failed to fetch event downloads'
        });
        fetchingRef.current.delete(eventId); // Allow retry on error
        setDownloadsCache((prev) => ({
          ...prev,
          [eventId]: { downloads: [], loading: false, loaded: true }
        }));
      }
    },
    [t, notifyError]
  );

  const handleExpandClick = useCallback(
    (eventId: number) => {
      if (expandedEventId === eventId) {
        setExpandedEventId(null);
      } else {
        setExpandedEventId(eventId);
        fetchEventDownloads(eventId);
      }
    },
    [expandedEventId, fetchEventDownloads]
  );

  const handleViewStats = useCallback(
    (event: Event) => {
      // Set the event filter to show only downloads tagged to this event
      setSelectedEventIds([event.id]);
      setTimeRange('live');

      // Navigate to dashboard via custom event
      window.dispatchEvent(
        new CustomEvent(APP_EVENTS.NAVIGATE_TO_TAB, { detail: { tab: 'dashboard' } })
      );
    },
    [setSelectedEventIds, setTimeRange]
  );

  // Section header component
  const SectionHeader: React.FC<{
    icon: React.ReactNode;
    title: string;
    count: number;
    color: string;
  }> = ({ icon, title, count, color }) => (
    <div className="flex items-center gap-2 mb-3">
      <div
        className="w-6 h-6 rounded-md flex items-center justify-center"
        style={{ backgroundColor: color.replace(')', '-subtle)') }}
      >
        {icon}
      </div>
      <h3 className="text-sm font-semibold flex items-center gap-2" style={{ color }}>
        {title}
        <span
          className="themed-badge badge-count"
          style={{
            backgroundColor: color.replace(')', '-subtle)'),
            color
          }}
        >
          {count}
        </span>
      </h3>
    </div>
  );

  if (events.length === 0) {
    return (
      <div className="text-center py-16">
        <div className="w-16 h-16 mx-auto mb-4 rounded-2xl flex items-center justify-center bg-[var(--theme-bg-tertiary)]">
          <CalendarClock className="w-8 h-8 text-[var(--theme-text-muted)]" />
        </div>
        <h3 className="text-lg font-semibold mb-2 text-[var(--theme-text-primary)]">
          {t('events.list.empty.title')}
        </h3>
        <p className="text-sm max-w-sm mx-auto text-[var(--theme-text-secondary)]">
          {t('events.list.empty.description')}
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-8">
      {/* Active Events */}
      {groupedEvents.active.length > 0 && (
        <div>
          <SectionHeader
            icon={<Zap className="w-3.5 h-3.5" style={{ color: 'var(--theme-status-success)' }} />}
            title={t('events.list.sections.active')}
            count={groupedEvents.active.length}
            color="var(--theme-status-success)"
          />
          <div className="space-y-3">
            {groupedEvents.active.map((event) => (
              <EventCard
                key={event.id}
                event={event}
                status="active"
                isExpanded={expandedEventId === event.id}
                cacheEntry={downloadsCache[event.id]}
                onExpandClick={handleExpandClick}
                onEditClick={onEventClick}
                onViewStatsClick={handleViewStats}
                formatDateTime={formatDateTime}
                formatDurationBetweenDates={formatDurationBetweenDates}
              />
            ))}
          </div>
        </div>
      )}

      {/* Upcoming Events */}
      {groupedEvents.upcoming.length > 0 && (
        <div>
          <SectionHeader
            icon={
              <CalendarClock className="w-3.5 h-3.5" style={{ color: 'var(--theme-primary)' }} />
            }
            title={t('events.list.sections.upcoming')}
            count={groupedEvents.upcoming.length}
            color="var(--theme-primary)"
          />
          <div className="space-y-3">
            {groupedEvents.upcoming.map((event) => (
              <EventCard
                key={event.id}
                event={event}
                status="upcoming"
                isExpanded={expandedEventId === event.id}
                cacheEntry={downloadsCache[event.id]}
                onExpandClick={handleExpandClick}
                onEditClick={onEventClick}
                onViewStatsClick={handleViewStats}
                formatDateTime={formatDateTime}
                formatDurationBetweenDates={formatDurationBetweenDates}
              />
            ))}
          </div>
        </div>
      )}

      {/* Past Events */}
      {groupedEvents.past.length > 0 && (
        <div>
          <SectionHeader
            icon={<History className="w-3.5 h-3.5" style={{ color: 'var(--theme-text-muted)' }} />}
            title={t('events.list.sections.past')}
            count={groupedEvents.past.length}
            color="var(--theme-text-muted)"
          />
          <div className="space-y-3">
            {groupedEvents.past.map((event) => (
              <EventCard
                key={event.id}
                event={event}
                status="past"
                isExpanded={expandedEventId === event.id}
                cacheEntry={downloadsCache[event.id]}
                onExpandClick={handleExpandClick}
                onEditClick={onEventClick}
                onViewStatsClick={handleViewStats}
                formatDateTime={formatDateTime}
                formatDurationBetweenDates={formatDurationBetweenDates}
              />
            ))}
          </div>
        </div>
      )}
    </div>
  );
};

export default EventList;
