import React from 'react';
import { useTranslation } from 'react-i18next';
import ThemeManager from '../theme/ThemeManager';

interface PreferencesSectionProps {
  isAdmin: boolean;
}

const PreferencesSection: React.FC<PreferencesSectionProps> = ({ isAdmin }) => {
  const { t } = useTranslation();

  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-preferences"
      aria-labelledby="tab-preferences"
    >
      {/* Section Header */}
      <div className="mb-6">
        <p className="text-themed-secondary text-sm">
          {t('management.sections.preferences.subtitle')}
        </p>
      </div>

      <ThemeManager isAdmin={isAdmin} />
    </div>
  );
};

export default PreferencesSection;
