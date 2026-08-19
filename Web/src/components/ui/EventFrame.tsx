import React from 'react';
import { useTranslation } from 'react-i18next';
import { Tooltip } from '@components/ui/Tooltip';
import { getEventColorVar } from '@utils/eventColors';

interface EventFrameEvent {
  id: number;
  name: string;
  colorIndex: number;
}

interface EventFrameProps {
  children: React.ReactNode;
  color: string;
  label: string;
  /** All active events - used to show tooltip with full list when multiple events are active */
  allEvents: EventFrameEvent[];
}

/**
 * Bordered frame whose top edge is a filled label bar carrying the event name
 * and live tag. Bar and border are normal flow, so the frame tracks content
 * height and the label stays aligned at every viewport width.
 */
const EventFrame: React.FC<EventFrameProps> = ({ children, color, label, allEvents }) => {
  const { t } = useTranslation();
  const hasMultipleEvents = allEvents.length > 1;

  const wrapperStyle = { '--event-frame-color': color } as React.CSSProperties;

  const bar = (
    <div className="event-frame-bar">
      <span className="event-frame-bar-dot event-frame-live-dot" />
      <span className="event-frame-bar-name">{label}</span>
      <span className="event-frame-bar-tag">{t('eventFrame.badge')}</span>
    </div>
  );

  return (
    <div className="event-frame-wrapper" style={wrapperStyle}>
      {hasMultipleEvents ? (
        <Tooltip
          position="bottom"
          offset={8}
          className="event-frame-bar-trigger"
          content={
            <div className="event-frame-tooltip">
              <div className="event-frame-tooltip-title">{t('eventFrame.activeEvents')}</div>
              <div className="event-frame-tooltip-list">
                {allEvents.map((event) => (
                  <div key={event.id} className="event-frame-tooltip-item">
                    <span
                      className="event-frame-tooltip-dot"
                      style={{ backgroundColor: getEventColorVar(event.colorIndex) }}
                    />
                    <span className="event-frame-tooltip-name">{event.name}</span>
                  </div>
                ))}
              </div>
            </div>
          }
        >
          {bar}
        </Tooltip>
      ) : (
        bar
      )}
      <div className="event-frame-content">{children}</div>
    </div>
  );
};

export default EventFrame;
