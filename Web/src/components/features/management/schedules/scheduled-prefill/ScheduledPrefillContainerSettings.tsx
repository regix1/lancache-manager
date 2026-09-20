import type { ReactNode } from 'react';

interface ScheduledPrefillContainerSettingsProps {
  children: ReactNode;
}

export function ScheduledPrefillContainerSettings({
  children
}: ScheduledPrefillContainerSettingsProps) {
  return (
    <div className="scheduled-prefill-dialog-content scheduled-prefill-container-settings">
      {children}
    </div>
  );
}
