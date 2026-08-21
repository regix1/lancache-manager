import React from 'react';

interface TabPanelProps {
  /** The management tab this panel belongs to, e.g. "clients" - drives id="panel-clients" and
   *  aria-labelledby="tab-clients" so it resolves against ManagementNav's id="tab-clients" button. */
  tabId: string;
  /** Extra class for a section's own layout rules, e.g. PrefillSessionsSection's
   *  .prefill-sessions-section or SchedulesSection's .schedules-section/-loading/-error. */
  className?: string;
  children: React.ReactNode;
}

// Management section wrapper shared by every tab's content, and by a section's own loading/error
// early returns. animate-fade-in is a live keyframe (styles/utilities/animations.css:112), not dead
// code - keep it. [21]
export const TabPanel: React.FC<TabPanelProps> = ({ tabId, className, children }) => (
  <div
    className={
      className
        ? `management-section animate-fade-in ${className}`
        : 'management-section animate-fade-in'
    }
    role="tabpanel"
    id={`panel-${tabId}`}
    aria-labelledby={`tab-${tabId}`}
  >
    {children}
  </div>
);
