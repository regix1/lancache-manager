import React, { useMemo } from 'react';
import { Calendar, Clock, ChevronRight, Zap, History, CalendarClock } from 'lucide-react';
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
    const remainingHours = hours % 24;

    if (days > 0) {
      if (remainingHours > 0) {
        return `${days}d ${remainingHours}h`;
      }
      return `${days} day${days > 1 ? 's' : ''}`;
    }
    return `${hours} hour${hours !== 1 ? 's' : ''}`;
  };

  const EventCard: React.FC<{ event: Event; status: 'active' | 'upcoming' | 'past'; index: number }> = ({ event, status, index }) => (
    <button
      onClick={() => onEventClick(event)}
      className="w-full text-left rounded-lg border transition-all duration-200 group overflow-hidden"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderColor: status === 'active' ? event.color : 'var(--theme-border-primary)',
        borderWidth: status === 'active' ? '2px' : '1px',
        opacity: status === 'past' ? 0.65 : 1,
        animationDelay: `${index * 50}ms`
      }}
      onMouseEnter={(e) => {
        e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)';
        e.currentTarget.style.transform = 'translateY(-1px)';
        e.currentTarget.style.boxShadow = '0 4px 12px rgba(0, 0, 0, 0.15)';
      }}
      onMouseLeave={(e) => {
        e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)';
        e.currentTarget.style.transform = 'translateY(0)';
        e.currentTarget.style.boxShadow = 'none';
      }}
    >
      {/* Active indicator bar */}
      {status === 'active' && (
        <div
          className="h-1"
          style={{
            background: `linear-gradient(90deg, ${event.color}, ${event.color}80)`
          }}
        />
      )}

      <div className="p-4">
        <div className="flex items-start justify-between gap-4">
          <div className="flex-1 min-w-0">
            {/* Header row with badge and name */}
            <div className="flex items-center gap-2.5 mb-2">
              {/* Event color badge - pill style */}
              <span
                className="inline-flex items-center gap-1.5 px-2 py-0.5 text-[10px] font-semibold rounded-full uppercase tracking-wide"
                style={{
                  backgroundColor: `${event.color}20`,
                  color: event.color,
                  border: `1px solid ${event.color}40`
                }}
              >
                {status === 'active' && (
                  <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ backgroundColor: event.color }} />
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
                className="text-sm mb-3 line-clamp-2"
                style={{ color: 'var(--theme-text-secondary)' }}
              >
                {event.description}
              </p>
            )}

            {/* Meta info row */}
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
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

          {/* Arrow indicator */}
          <div
            className="flex-shrink-0 w-8 h-8 rounded-lg flex items-center justify-center transition-all duration-200"
            style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
          >
            <ChevronRight
              className="w-4 h-4 transition-transform group-hover:translate-x-0.5"
              style={{ color: 'var(--theme-text-secondary)' }}
            />
          </div>
        </div>
      </div>
    </button>
  );

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
              <EventCard key={event.id} event={event} status="active" index={index} />
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
              <EventCard key={event.id} event={event} status="upcoming" index={index} />
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
              <EventCard key={event.id} event={event} status="past" index={index} />
            ))}
          </div>
        </div>
      )}
    </div>
  );
};

export default EventList;
