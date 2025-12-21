import React from 'react';
import EventBadge from './EventBadge';
import type { EventSummary } from '../../../types';

interface DownloadBadgesProps {
  events: EventSummary[];
  onEventClick?: (event: EventSummary) => void;
  maxVisible?: number;
  size?: 'sm' | 'md';
}

const DownloadBadges: React.FC<DownloadBadgesProps> = ({
  events,
  onEventClick,
  maxVisible = 3,
  size = 'sm'
}) => {
  if (events.length === 0) {
    return null;
  }

  // Calculate how many to show
  const eventsToShow = events.slice(0, Math.min(events.length, maxVisible));
  const hiddenCount = events.length - eventsToShow.length;

  return (
    <div className="flex items-center gap-1 flex-wrap">
      {/* Event badges */}
      {eventsToShow.map((event) => (
        <EventBadge
          key={`event-${event.id}`}
          event={event}
          onClick={onEventClick ? () => onEventClick(event) : undefined}
          size={size}
        />
      ))}

      {/* Hidden count indicator */}
      {hiddenCount > 0 && (
        <span
          className={`inline-flex items-center justify-center rounded-full bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)] font-medium ${
            size === 'sm' ? 'px-1.5 py-0.5 text-[10px]' : 'px-2 py-1 text-xs'
          }`}
          title={`${hiddenCount} more badge${hiddenCount > 1 ? 's' : ''}`}
        >
          +{hiddenCount}
        </span>
      )}
    </div>
  );
};

export default DownloadBadges;
