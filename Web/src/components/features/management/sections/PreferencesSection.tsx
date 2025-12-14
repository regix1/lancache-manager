import React, { Suspense } from 'react';
import { Card } from '@components/ui/Card';
import ThemeManager from '../theme/ThemeManager';
import GcManager from '../gc/GcManager';

interface PreferencesSectionProps {
  isAuthenticated: boolean;
  optimizationsEnabled: boolean;
}

const PreferencesSection: React.FC<PreferencesSectionProps> = ({
  isAuthenticated,
  optimizationsEnabled
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
          Preferences
        </h2>
        <p className="text-themed-secondary text-sm">
          Customize themes, visual settings, and application behavior
        </p>
      </div>

      {/* Subsection: Theme Management */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div
            className="w-1 h-5 rounded-full"
            style={{ backgroundColor: 'var(--theme-primary)' }}
          />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            Theme Management
          </h3>
        </div>

        <ThemeManager isAuthenticated={isAuthenticated} />
      </div>

      {/* Subsection: Optimizations (Conditional) */}
      {optimizationsEnabled && (
        <div>
          <div className="flex items-center gap-2 mb-4">
            <div
              className="w-1 h-5 rounded-full"
              style={{ backgroundColor: 'var(--theme-icon-orange)' }}
            />
            <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
              Performance Optimizations
            </h3>
          </div>

          <Suspense
            fallback={
              <Card>
                <div className="flex items-center justify-center py-8">
                  <div className="text-themed-muted">Loading GC settings...</div>
                </div>
              </Card>
            }
          >
            <GcManager isAuthenticated={isAuthenticated} />
          </Suspense>
        </div>
      )}
    </div>
  );
};

export default PreferencesSection;
