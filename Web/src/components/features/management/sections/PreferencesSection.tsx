import React from 'react';
import ThemeManager from '../theme/ThemeManager';

interface PreferencesSectionProps {
  isAdmin: boolean;
}

const PreferencesSection: React.FC<PreferencesSectionProps> = ({ isAdmin }) => {
  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-preferences"
      aria-labelledby="tab-preferences"
    >
      <ThemeManager isAdmin={isAdmin} />
    </div>
  );
};

export default PreferencesSection;
