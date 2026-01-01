import React from 'react';
import ThemeManager from '../theme/ThemeManager';

interface PreferencesSectionProps {
  isAuthenticated: boolean;
}

const PreferencesSection: React.FC<PreferencesSectionProps> = ({
  isAuthenticated
}) => {
  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-preferences"
      aria-labelledby="tab-preferences"
    >
      {/* Section Header */}
      <div className="mb-6">
        <h2 className="text-xl font-semibold text-themed-primary mb-1">
          Theme
        </h2>
        <p className="text-themed-secondary text-sm">
          Customize themes and visual settings
        </p>
      </div>

      <ThemeManager isAuthenticated={isAuthenticated} />
    </div>
  );
};

export default PreferencesSection;
