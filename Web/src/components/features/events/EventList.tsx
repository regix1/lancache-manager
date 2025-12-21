import React, { useMemo, useState, useCallback } from 'react';
import { Calendar, Clock, ChevronRight, Zap, History, CalendarClock, Loader2, Pencil } from 'lucide-react';
import { useTimezone } from '@contexts/TimezoneContext';
import { formatBytes } from '@utils/formatters';
import { getEventColorStyles, getEventColorVar } from '@utils/eventColors';
import ApiService from '@services/api.service';
import type { Event, Download } from '../../../types';

interface EventDownloadsCache {
  [eventId: number]: {
    downloads: Download[];
    loading: boolean;
    loaded: boolean;
  };
}

// Group downloads by game name - moved outside component
const groupDownloadsByGame = (downloads: Download[]) => {
  const grouped: { [key: string]: { name: string; service: string; totalBytes: number; count: number } } = {};

  downloads.forEach(d => {
    const key = `${d.service}-${d.gameName || 'Unknown'}`;
    if (!grouped[key]) {
      grouped[key] = {
        name: d.gameName || 'Unknown',
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
  index: number;
  isExpanded: boolean;
  cacheEntry: EventDownloadsCache[number] | undefined;
  onExpandClick: () => void;
  onEditClick: () => void;
  formatDateTime: (dateStr: string) => string;
  formatDuration: (startStr: string, endStr: string) => string;
}

const EventCard: React.FC<EventCardProps> = ({
  event,
  status,
  index,
  isExpanded,
  cacheEntry,
  onExpandClick,
  onEditClick,
  formatDateTime,
  formatDuration
}) => {
  const downloads = cacheEntry?.downloads || [];
  const isLoading = cacheEntry?.loading || false;
  const groupedDownloads = groupDownloadsByGame(downloads);

  const colorVar = getEventColorVar(event.colorIndex);

  return (
    <div
      className="rounded-lg border transition-all duration-200 overflow-hidden cursor-pointer"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderColor: isExpanded ? colorVar : (status === 'active' ? colorVar : 'var(--theme-border-primary)'),
        borderWidth: status === 'active' || isExpanded ? '2px' : '1px',
        opacity: status === 'past' ? 0.65 : 1,
        animationDelay: `${index * 50}ms`
      }}
      onClick={onExpandClick}
    >
      {/* Active indicator bar */}
      {status === 'active' && (
        <div
          className="h-1"
          style={{
            background: `linear-gradient(90deg, ${colorVar}, color-mix(in srgb, ${colorVar} 80%, transparent))`
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
                className={`w-4 h-4 flex-shrink-0 transition-transform duration-200 ${isExpanded ? 'rotate-90' : ''}`}
                style={{ color: 'var(--theme-text-secondary)' }}
              />

              {/* Event color badge - pill style */}
              <span
                className="inline-flex items-center gap-1.5 px-2 py-0.5 text-[10px] font-semibold rounded-full uppercase tracking-wide"
                style={getEventColorStyles(event.colorIndex)}
              >
                {status === 'active' && (
                  <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ backgroundColor: colorVar }} />
                )}
                {status === 'active' ? 'Live' : status === 'upcoming' ? 'Upcoming' : 'Ended'}
              </span>

              <h3
                className="font-semibold truncate"
                style={{ color: 'var(--theme-text-primary)' }}
              >
                {event.name}
              </h3>
            </div>

            {/* Description */}
            {event.description && (
              <p
                className="text-sm mb-3 line-clamp-2 ml-6"
                style={{ color: 'var(--theme-text-secondary)' }}
              >
                {event.description}
              </p>
            )}

            {/* Meta info row */}
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1 ml-6">
              <div
                className="flex items-center gap-1.5 text-xs"
                style={{ color: 'var(--theme-text-muted)' }}
              >
                <Calendar className="w-3.5 h-3.5" />
                <span>{formatDateTime(event.startTimeUtc)}</span>
              </div>
              <div
                className="flex items-center gap-1.5 text-xs"
                style={{ color: 'var(--theme-text-muted)' }}
              >
                <Clock className="w-3.5 h-3.5" />
                <span>{formatDuration(event.startTimeUtc, event.endTimeUtc)}</span>
              </div>
            </div>
          </div>

          {/* Edit button */}
          <button
            onClick={(e) => {
              e.stopPropagation();
              onEditClick();
            }}
            className="flex-shrink-0 w-8 h-8 rounded-lg flex items-center justify-center transition-all duration-200 hover:bg-[var(--theme-bg-hover)]"
            style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            title="Edit event"
          >
            <Pencil
              className="w-4 h-4"
              style={{ color: 'var(--theme-text-secondary)' }}
            />
          </button>
        </div>
      </div>

      {/* Expanded downloads section */}
      {isExpanded && (
        <div
          className="border-t px-4 py-3"
          style={{
            borderColor: 'var(--theme-border-secondary)',
            backgroundColor: 'var(--theme-bg-tertiary)'
          }}
        >
          {isLoading ? (
            <div className="flex items-center justify-center py-4 gap-2">
              <Loader2 className="w-4 h-4 animate-spin" style={{ color: 'var(--theme-primary)' }} />
              <span className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                Loading downloads...
              </span>
            </div>
          ) : groupedDownloads.length === 0 ? (
            <p className="text-sm text-center py-4" style={{ color: 'var(--theme-text-muted)' }}>
              No downloads recorded during this event
            </p>
          ) : (
            <div className="space-y-2">
              <div className="flex items-center justify-between mb-2">
                <span className="text-xs font-medium" style={{ color: 'var(--theme-text-secondary)' }}>
                  Games downloaded during event
                </span>
                <span className="text-xs px-2 py-0.5 rounded-full" style={{ backgroundColor: 'var(--theme-bg-secondary)', color: 'var(--theme-text-muted)' }}>
                  {groupedDownloads.length} game{groupedDownloads.length !== 1 ? 's' : ''}
                </span>
              </div>
              {groupedDownloads.slice(0, 10).map((game, idx) => (
                <div
                  key={idx}
                  className="flex items-center justify-between py-2 px-3 rounded-lg"
                  style={{ backgroundColor: 'var(--theme-bg-secondary)' }}
                >
                  <div className="flex items-center gap-2 min-w-0">
                    <span
                      className="px-1.5 py-0.5 text-[10px] font-bold rounded"
                      style={{
                        backgroundColor: 'var(--theme-bg-tertiary)',
                        color: 'var(--theme-text-secondary)'
                      }}
                    >
                      {game.service.toUpperCase()}
                    </span>
                    <span className="text-sm font-medium truncate" style={{ color: 'var(--theme-text-primary)' }}>
                      {game.name}
                    </span>
                  </div>
                  <div className="flex items-center gap-3 flex-shrink-0">
                    <span className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                      {game.count}x
                    </span>
                    <span className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
                      {formatBytes(game.totalBytes)}
                    </span>
                  </div>
                </div>
              ))}
              {groupedDownloads.length > 10 && (
                <p className="text-xs text-center pt-2" style={{ color: 'var(--theme-text-muted)' }}>
                  +{groupedDownloads.length - 10} more games
                </p>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
};

interface EventListProps {
  events: Event[];
  onEventClick: (event: Event) => void;
}

const EventList: React.FC<EventListProps> = ({ events, onEventClick }) => {
  const { use24HourFormat } = useTimezone();
  const [expandedEventId, setExpandedEventId] = useState<number | null>(null);
  const [downloadsCache, setDownloadsCache] = useState<EventDownloadsCache>({});
  const fetchingRef = React.useRef<Set<number>>(new Set());

  // Group events by status: active, upcoming, past
  const groupedEvents = useMemo(() => {
    const now = new Date();
    const active: Event[] = [];
    const upcoming: Event[] = [];
    const past: Event[] = [];

    events.forEach(event => {
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
    upcoming.sort((a, b) => new Date(a.startTimeUtc).getTime() - new Date(b.startTimeUtc).getTime());
    // Sort past by end date (most recent first)
    past.sort((a, b) => new Date(b.endTimeUtc).getTime() - new Date(a.endTimeUtc).getTime());

    return { active, upcoming, past };
  }, [events]);

  const formatDateTime = useCallback((dateStr: string) => {
    const date = new Date(dateStr);
    return date.toLocaleString(undefined, {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
      hour12: !use24HourFormat
    });
  }, [use24HourFormat]);

  const formatDuration = useCallback((startStr: string, endStr: string) => {
    const start = new Date(startStr);
    const end = new Date(endStr);
    const diffMs = end.getTime() - start.getTime();
    const hours = Math.floor(diffMs / (1000 * 60 * 60));
    const days = Math.floor(hours / 24);
    const remainingHours = hours % 24;

    if (days > 0) {
      if (remainingHours > 0) {
        return `${days}d ${remainingHours}h`;
      }
      return `${days} day${days > 1 ? 's' : ''}`;
    }
    return `${hours} hour${hours !== 1 ? 's' : ''}`;
  }, []);

  const fetchEventDownloads = useCallback(async (eventId: number) => {
    // Already fetching or fetched
    if (fetchingRef.current.has(eventId)) {
      return;
    }
    fetchingRef.current.add(eventId);

    setDownloadsCache(prev => ({
      ...prev,
      [eventId]: { downloads: [], loading: true, loaded: false }
    }));

    try {
      const response = await fetch(`/api/events/${eventId}/downloads`, ApiService.getFetchOptions());
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const downloads = await response.json();
      setDownloadsCache(prev => ({
        ...prev,
        [eventId]: { downloads: Array.isArray(downloads) ? downloads : [], loading: false, loaded: true }
      }));
    } catch (error) {
      console.error('Failed to fetch event downloads:', error);
      fetchingRef.current.delete(eventId); // Allow retry on error
      setDownloadsCache(prev => ({
        ...prev,
        [eventId]: { downloads: [], loading: false, loaded: true }
      }));
    }
  }, []);

  const handleExpandClick = useCallback((eventId: number) => {
    if (expandedEventId === eventId) {
      setExpandedEventId(null);
    } else {
      setExpandedEventId(eventId);
      fetchEventDownloads(eventId);
    }
  }, [expandedEventId, fetchEventDownloads]);

  // Section header component
  const SectionHeader: React.FC<{
    icon: React.ReactNode;
    title: string;
    count: number;
    color: string;
    pulse?: boolean;
  }> = ({ icon, title, count, color, pulse }) => (
    <div className="flex items-center gap-2 mb-3">
      <div
        className="w-6 h-6 rounded-md flex items-center justify-center"
        style={{ backgroundColor: `color-mix(in srgb, ${color} 15%, transparent)` }}
      >
        {icon}
      </div>
      <h3
        className="text-sm font-semibold flex items-center gap-2"
        style={{ color }}
      >
        {pulse && (
          <span className="w-2 h-2 rounded-full animate-pulse" style={{ backgroundColor: color }} />
        )}
        {title}
        <span
          className="px-1.5 py-0.5 text-[10px] rounded-full font-medium"
          style={{
            backgroundColor: `color-mix(in srgb, ${color} 12%, transparent)`,
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
        <div
          className="w-16 h-16 mx-auto mb-4 rounded-2xl flex items-center justify-center"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          <CalendarClock className="w-8 h-8" style={{ color: 'var(--theme-text-muted)' }} />
        </div>
        <h3
          className="text-lg font-semibold mb-2"
          style={{ color: 'var(--theme-text-primary)' }}
        >
          No Events
        </h3>
        <p
          className="text-sm max-w-sm mx-auto"
          style={{ color: 'var(--theme-text-secondary)' }}
        >
          Create your first event to start tracking downloads during LAN parties.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-8">
      {/* Active Events */}
      {groupedEvents.active.length > 0 && (
        <div className="animate-fadeIn">
          <SectionHeader
            icon={<Zap className="w-3.5 h-3.5" style={{ color: 'var(--theme-status-success)' }} />}
            title="Active Events"
            count={groupedEvents.active.length}
            color="var(--theme-status-success)"
            pulse
          />
          <div className="space-y-3">
            {groupedEvents.active.map((event, index) => (
              <EventCard
                key={event.id}
                event={event}
                status="active"
                index={index}
                isExpanded={expandedEventId === event.id}
                cacheEntry={downloadsCache[event.id]}
                onExpandClick={() => handleExpandClick(event.id)}
                onEditClick={() => onEventClick(event)}
                formatDateTime={formatDateTime}
                formatDuration={formatDuration}
              />
            ))}
          </div>
        </div>
      )}

      {/* Upcoming Events */}
      {groupedEvents.upcoming.length > 0 && (
        <div className="animate-fadeIn" style={{ animationDelay: '100ms' }}>
          <SectionHeader
            icon={<CalendarClock className="w-3.5 h-3.5" style={{ color: 'var(--theme-primary)' }} />}
            title="Upcoming Events"
            count={groupedEvents.upcoming.length}
            color="var(--theme-primary)"
          />
          <div className="space-y-3">
            {groupedEvents.upcoming.map((event, index) => (
              <EventCard
                key={event.id}
                event={event}
                status="upcoming"
                index={index}
                isExpanded={expandedEventId === event.id}
                cacheEntry={downloadsCache[event.id]}
                onExpandClick={() => handleExpandClick(event.id)}
                onEditClick={() => onEventClick(event)}
                formatDateTime={formatDateTime}
                formatDuration={formatDuration}
              />
            ))}
          </div>
        </div>
      )}

      {/* Past Events */}
      {groupedEvents.past.length > 0 && (
        <div className="animate-fadeIn" style={{ animationDelay: '200ms' }}>
          <SectionHeader
            icon={<History className="w-3.5 h-3.5" style={{ color: 'var(--theme-text-muted)' }} />}
            title="Past Events"
            count={groupedEvents.past.length}
            color="var(--theme-text-muted)"
          />
          <div className="space-y-3">
            {groupedEvents.past.map((event, index) => (
              <EventCard
                key={event.id}
                event={event}
                status="past"
                index={index}
                isExpanded={expandedEventId === event.id}
                cacheEntry={downloadsCache[event.id]}
                onExpandClick={() => handleExpandClick(event.id)}
                onEditClick={() => onEventClick(event)}
                formatDateTime={formatDateTime}
                formatDuration={formatDuration}
              />
            ))}
          </div>
        </div>
      )}
    </div>
  );
};

export default EventList;
