import React, { useMemo, useCallback } from 'react';
import { Calendar, Clock, ChevronRight } from 'lucide-react';
import { useTimezone } from '@contexts/TimezoneContext';
import type { Event } from '../../../types';

interface EventListProps {
  events: Event[];
  onEventClick: (event: Event) => void;
}

const EventList: React.FC<EventListProps> = ({ events, onEventClick }) => {
  const { use24HourFormat } = useTimezone();
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

  const formatDateTime = (dateStr: string) => {
    const date = new Date(dateStr);
    return date.toLocaleString(undefined, {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
      hour12: !use24HourFormat
    });
  };

  const formatDuration = (startStr: string, endStr: string) => {
    const start = new Date(startStr);
    const end = new Date(endStr);
    const diffMs = end.getTime() - start.getTime();
    const hours = Math.floor(diffMs / (1000 * 60 * 60));
    const days = Math.floor(hours / 24);

    if (days > 0) {
      return `${days} day${days > 1 ? 's' : ''}`;
    }
    return `${hours} hour${hours > 1 ? 's' : ''}`;
  };

  const EventCard: React.FC<{ event: Event; status: 'active' | 'upcoming' | 'past' }> = ({ event, status }) => (
    <button
      onClick={() => onEventClick(event)}
      className="w-full text-left p-4 rounded-lg border transition-all hover:shadow-md group"
      style={{
        backgroundColor: status === 'past' ? 'var(--theme-bg-tertiary)' : 'var(--theme-bg-secondary)',
        borderColor: status === 'active' ? event.color : 'var(--theme-border-primary)',
        borderWidth: status === 'active' ? '2px' : '1px',
        opacity: status === 'past' ? 0.7 : 1
      }}
    >
      <div className="flex items-start justify-between gap-4">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1">
            <div
              className="w-3 h-3 rounded-full flex-shrink-0"
              style={{ backgroundColor: event.color }}
            />
            <h3 className="font-medium text-[var(--theme-text-primary)] truncate">
              {event.name}
            </h3>
            {status === 'active' && (
              <span className="px-2 py-0.5 text-xs rounded-full bg-[var(--theme-status-success)]/20 text-[var(--theme-status-success)]">
                Active
              </span>
            )}
          </div>

          {event.description && (
            <p className="text-sm text-[var(--theme-text-secondary)] mb-2 line-clamp-2">
              {event.description}
            </p>
          )}

          <div className="flex flex-wrap gap-4 text-xs text-[var(--theme-text-secondary)]">
            <div className="flex items-center gap-1">
              <Calendar className="w-3.5 h-3.5" />
              <span>{formatDateTime(event.startTimeUtc)}</span>
            </div>
            <div className="flex items-center gap-1">
              <Clock className="w-3.5 h-3.5" />
              <span>{formatDuration(event.startTimeUtc, event.endTimeUtc)}</span>
            </div>
          </div>
        </div>

        <ChevronRight className="w-5 h-5 text-[var(--theme-text-secondary)] group-hover:text-[var(--theme-text-primary)] transition-colors flex-shrink-0" />
      </div>
    </button>
  );

  if (events.length === 0) {
    return (
      <div className="text-center py-12">
        <Calendar className="w-12 h-12 mx-auto mb-4 text-[var(--theme-text-secondary)]" />
        <h3 className="text-lg font-medium text-[var(--theme-text-primary)] mb-2">No Events</h3>
        <p className="text-sm text-[var(--theme-text-secondary)]">
          Create your first event to start tracking downloads during LAN parties.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {/* Active Events */}
      {groupedEvents.active.length > 0 && (
        <div>
          <h3 className="text-sm font-medium text-[var(--theme-status-success)] mb-3 flex items-center gap-2">
            <div className="w-2 h-2 rounded-full bg-[var(--theme-status-success)] animate-pulse" />
            Active Events ({groupedEvents.active.length})
          </h3>
          <div className="space-y-2">
            {groupedEvents.active.map(event => (
              <EventCard key={event.id} event={event} status="active" />
            ))}
          </div>
        </div>
      )}

      {/* Upcoming Events */}
      {groupedEvents.upcoming.length > 0 && (
        <div>
          <h3 className="text-sm font-medium text-[var(--theme-text-secondary)] mb-3">
            Upcoming Events ({groupedEvents.upcoming.length})
          </h3>
          <div className="space-y-2">
            {groupedEvents.upcoming.map(event => (
              <EventCard key={event.id} event={event} status="upcoming" />
            ))}
          </div>
        </div>
      )}

      {/* Past Events */}
      {groupedEvents.past.length > 0 && (
        <div>
          <h3 className="text-sm font-medium text-[var(--theme-text-secondary)] mb-3">
            Past Events ({groupedEvents.past.length})
          </h3>
          <div className="space-y-2">
            {groupedEvents.past.map(event => (
              <EventCard key={event.id} event={event} status="past" />
            ))}
          </div>
        </div>
      )}
    </div>
  );
};

export default EventList;
