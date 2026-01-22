import React from 'react';
import { useTranslation } from 'react-i18next';
import EventFrame from '@components/ui/EventFrame';
import { useEvents } from '@contexts/EventContext';
import { getEventColorVar } from '@utils/eventColors';

interface ActiveEventBorderProps {
  children: React.ReactNode;
  enabled?: boolean;
}

/**
 * Wraps content with an animated border when one or more events are active.
 * Shows the first event name with "+X more" if multiple events are running.
 */
const ActiveEventBorder: React.FC<ActiveEventBorderProps> = ({ children, enabled = true }) => {
  const { t } = useTranslation();
  const { activeEvents } = useEvents();

  // Get the first active event (if any)
  const activeEvent = enabled && activeEvents.length > 0 ? activeEvents[0] : null;
  const additionalCount = activeEvents.length - 1;

  if (!activeEvent) {
    return <>{children}</>;
  }

  // Build label: "Event Name" or "Event Name +2"
  const label = additionalCount > 0
    ? t('eventFrame.labelWithMore', {
        name: activeEvent.name,
        count: additionalCount,
        defaultValue: '{{name}} +{{count}}'
      })
    : activeEvent.name;

  // Map events to the format EventFrame expects
  const allEventsForFrame = activeEvents.map(event => ({
    id: event.id,
    name: event.name,
    colorIndex: event.colorIndex
  }));

  return (
    <EventFrame
      color={getEventColorVar(activeEvent.colorIndex)}
      label={label}
      allEvents={allEventsForFrame}
    >
      {children}
    </EventFrame>
  );
};

export default ActiveEventBorder;
