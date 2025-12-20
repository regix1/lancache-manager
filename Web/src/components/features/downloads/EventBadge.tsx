import React from 'react';
import { CalendarDays } from 'lucide-react';
import type { EventSummary } from '../../../types';

interface EventBadgeProps {
  event: EventSummary;
  onClick?: () => void;
  size?: 'sm' | 'md';
  showIcon?: boolean;
}

const EventBadge: React.FC<EventBadgeProps> = ({
  event,
  onClick,
  size = 'sm',
  showIcon = false
}) => {
  const sizeClasses = size === 'sm'
    ? 'px-1.5 py-0.5 text-[10px]'
    : 'px-2 py-1 text-xs';

  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full font-medium transition-all ${sizeClasses} ${onClick ? 'cursor-pointer hover:opacity-80' : ''}`}
      style={{
        backgroundColor: `${event.color}20`,
        color: event.color,
        border: `1px solid ${event.color}40`
      }}
      onClick={onClick}
      title={`${event.name}${event.autoTagged ? ' (auto-tagged)' : ' (manually tagged)'}`}
    >
      {showIcon && <CalendarDays className="w-3 h-3" />}
      <span className="truncate max-w-[100px]">{event.name}</span>
      {event.autoTagged && (
        <span className="opacity-60 text-[8px]">auto</span>
      )}
    </span>
  );
};

export default EventBadge;
