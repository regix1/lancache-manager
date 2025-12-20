import React from 'react';
import TagBadge from './TagBadge';
import EventBadge from './EventBadge';
import type { TagSummary, EventSummary } from '../../../types';

interface DownloadBadgesProps {
  tags: TagSummary[];
  events: EventSummary[];
  onTagClick?: (tag: TagSummary) => void;
  onEventClick?: (event: EventSummary) => void;
  onTagRemove?: (tag: TagSummary) => void;
  maxVisible?: number;
  showRemove?: boolean;
  size?: 'sm' | 'md';
}

const DownloadBadges: React.FC<DownloadBadgesProps> = ({
  tags,
  events,
  onTagClick,
  onEventClick,
  onTagRemove,
  maxVisible = 3,
  showRemove = false,
  size = 'sm'
}) => {
  const totalBadges = tags.length + events.length;

  if (totalBadges === 0) {
    return null;
  }

  // Calculate how many to show
  const eventsToShow = events.slice(0, Math.min(events.length, maxVisible));
  const remainingSlots = maxVisible - eventsToShow.length;
  const tagsToShow = tags.slice(0, Math.max(0, remainingSlots));
  const hiddenCount = totalBadges - eventsToShow.length - tagsToShow.length;

  return (
    <div className="flex items-center gap-1 flex-wrap">
      {/* Event badges first */}
      {eventsToShow.map((event) => (
        <EventBadge
          key={`event-${event.id}`}
          event={event}
          onClick={onEventClick ? () => onEventClick(event) : undefined}
          size={size}
        />
      ))}

      {/* Tag badges */}
      {tagsToShow.map((tag) => (
        <TagBadge
          key={`tag-${tag.id}`}
          tag={tag}
          onClick={onTagClick ? () => onTagClick(tag) : undefined}
          onRemove={onTagRemove ? () => onTagRemove(tag) : undefined}
          showRemove={showRemove}
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
