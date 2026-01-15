import React from 'react';
import EventFrame from '@components/ui/EventFrame';
import { useEvents } from '@contexts/EventContext';
import { getEventColorVar } from '@utils/eventColors';

interface ActiveEventBorderProps {
  children: React.ReactNode;
  enabled?: boolean;
}

/**
 * Wraps content with an animated dashed border when an event is active.
 */
const ActiveEventBorder: React.FC<ActiveEventBorderProps> = ({ children, enabled = true }) => {
  const { activeEvents } = useEvents();

  // Get the first active event (if any)
  const activeEvent = enabled && activeEvents.length > 0 ? activeEvents[0] : null;

  if (!activeEvent) {
    return <>{children}</>;
  }

  return (
    <EventFrame
      color={getEventColorVar(activeEvent.colorIndex)}
      label={activeEvent.name}
    >
      {children}
    </EventFrame>
  );
};

export default ActiveEventBorder;
