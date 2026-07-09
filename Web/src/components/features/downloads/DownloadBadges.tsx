import React from 'react';
import { useTranslation } from 'react-i18next';
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
  const { t } = useTranslation();
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
          className="themed-badge status-badge-neutral badge-count"
          title={t('downloads.tab.badges.more', { count: hiddenCount })}
        >
          +{hiddenCount}
        </span>
      )}
    </div>
  );
};

export default DownloadBadges;
