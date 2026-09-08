import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown } from 'lucide-react';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import './BackgroundTaskControls.css';

export const BackgroundTaskControls: React.FC<{
  count: number;
  children: React.ReactNode;
}> = ({ count, children }) => {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);

  return (
    <div className="background-task-controls">
      <button
        type="button"
        className="background-task-controls__header"
        aria-expanded={open}
        onClick={() => setOpen((previous) => !previous)}
      >
        <span>{t('common.notifications.backgroundTasksRunning', { count })}</span>
        <ChevronDown
          className={`background-task-controls__chevron${open ? ' is-open' : ''}`}
          aria-hidden="true"
        />
      </button>
      <CollapsibleRegion open={open} contentClassName="background-task-controls__content">
        {children}
      </CollapsibleRegion>
    </div>
  );
};
