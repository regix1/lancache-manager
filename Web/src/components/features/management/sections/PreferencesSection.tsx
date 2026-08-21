import React from 'react';
import { TabPanel } from '@components/features/management/TabPanel';
import ThemeManager from '../theme/ThemeManager';

interface PreferencesSectionProps {
  isAdmin: boolean;
}

const PreferencesSection: React.FC<PreferencesSectionProps> = ({ isAdmin }) => {
  return (
    <TabPanel tabId="preferences">
      <ThemeManager isAdmin={isAdmin} />
    </TabPanel>
  );
};

export default PreferencesSection;
