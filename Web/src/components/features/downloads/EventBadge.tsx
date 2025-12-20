import React from 'react';
import type { EventSummary } from '../../../types';

interface EventBadgeProps {
  event: EventSummary;
  onClick?: () => void;
  size?: 'sm' | 'md';
}

const EventBadge: React.FC<EventBadgeProps> = ({
  event,
  onClick,
  size = 'sm'
}) => {
  const sizeClasses = size === 'sm'
    ? 'px-1.5 py-0.5 text-xs'
    : 'px-2 py-1 text-xs';

  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-md font-medium transition-all flex-shrink-0 ${sizeClasses} ${onClick ? 'cursor-pointer hover:opacity-80' : ''}`}
      style={{
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-text-secondary)',
        border: '1px solid var(--theme-border-secondary)'
      }}
      onClick={onClick}
      title={`${event.name}${event.autoTagged ? ' (auto-tagged)' : ' (manually tagged)'}`}
    >
      {/* Color indicator dot */}
      <span
        className="w-2 h-2 rounded-full flex-shrink-0"
        style={{ backgroundColor: event.color }}
      />
      <span className="truncate max-w-[100px]">{event.name}</span>
      {event.autoTagged && (
        <span className="opacity-60">auto</span>
      )}
    </span>
  );
};

export default EventBadge;
