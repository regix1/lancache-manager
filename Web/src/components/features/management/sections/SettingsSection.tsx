import React, { useCallback, useState, Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { Shield, Sparkles, Settings, Gauge } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import Badge from '@components/ui/Badge';
import { useMockMode } from '@contexts/useMockMode';
import { useAuth } from '@contexts/useAuth';
import { useNotifications } from '@contexts/notifications';
import AuthenticationManager from '../steam/AuthenticationManager';
import DisplayPreferences from './DisplayPreferences';
import GcManager from '../gc/GcManager';

interface SettingsSectionProps {
  optimizationsEnabled: boolean;
  isAdmin: boolean;
}

const SettingsSection: React.FC<SettingsSectionProps> = ({ optimizationsEnabled, isAdmin }) => {
  const { t } = useTranslation();
  const { mockMode, setMockMode } = useMockMode();
  const { authenticationEnabled } = useAuth();
  const { addNotification } = useNotifications();

  const [apiAuthExpanded, setApiAuthExpanded] = useState(false);
  const [demoModeExpanded, setDemoModeExpanded] = useState(false);
  const [displayPrefsExpanded, setDisplayPrefsExpanded] = useState(false);
  const [performanceExpanded, setPerformanceExpanded] = useState(false);

  const handleError = useCallback(
    (message: string) => {
      addNotification({
        type: 'generic',
        status: 'failed',
        message,
        details: { notificationType: 'error' }
      });
    },
    [addNotification]
  );

  const handleSuccess = useCallback(
    (message: string) => {
      addNotification({
        type: 'generic',
        status: 'completed',
        message,
        details: { notificationType: 'success' }
      });
    },
    [addNotification]
  );

  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-settings"
      aria-labelledby="tab-settings"
    >
      {/* SYSTEM */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-blue)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.settings.groupSystem')}
          </h3>
        </div>

        <div className="space-y-4">
          <AccordionSection
            title={t('management.sections.settings.apiAuth')}
            description={t('management.sections.settings.apiAuthDesc')}
            icon={Shield}
            iconColor="var(--theme-icon-green)"
            isExpanded={apiAuthExpanded}
            onToggle={() => setApiAuthExpanded((prev) => !prev)}
            badge={
              <Badge variant={authenticationEnabled ? 'success' : 'neutral'}>
                {authenticationEnabled
                  ? t('management.sections.settings.enabled')
                  : t('management.sections.settings.disabled')}
              </Badge>
            }
          >
            <AuthenticationManager onError={handleError} onSuccess={handleSuccess} />
          </AccordionSection>

          <AccordionSection
            title={t('management.sections.settings.demoMode')}
            description={t('management.sections.settings.demoModeDesc')}
            icon={Sparkles}
            iconColor="var(--theme-icon-purple)"
            isExpanded={demoModeExpanded}
            onToggle={() => setDemoModeExpanded((prev) => !prev)}
            badge={
              <Badge variant={mockMode ? 'success' : 'neutral'}>
                {mockMode
                  ? t('management.sections.settings.enabled')
                  : t('management.sections.settings.disabled')}
              </Badge>
            }
          >
            <div className="p-4 rounded-lg bg-themed-tertiary">
              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                <div className="flex-1">
                  <p className="text-themed-primary text-sm font-medium">
                    {t('management.sections.settings.mockData')}
                  </p>
                </div>
                <Button
                  onClick={() => setMockMode(!mockMode)}
                  variant="filled"
                  color={mockMode ? 'blue' : 'gray'}
                  className="w-full sm:w-36"
                >
                  {mockMode
                    ? t('management.sections.settings.enabled')
                    : t('management.sections.settings.disabled')}
                </Button>
              </div>
            </div>
            {mockMode && (
              <div className="mt-4">
                <Alert color="blue">
                  <span className="text-sm">
                    {t('management.sections.settings.mockModeActive')}
                  </span>
                </Alert>
              </div>
            )}
          </AccordionSection>
        </div>
      </div>

      {/* PREFERENCES */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-purple)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.settings.groupPreferences')}
          </h3>
        </div>

        <div className="space-y-4">
          <AccordionSection
            title={t('management.sections.settings.displayPreferences')}
            description={t('management.sections.settings.displayPreferencesDesc')}
            icon={Settings}
            iconColor="var(--theme-icon-blue)"
            isExpanded={displayPrefsExpanded}
            onToggle={() => setDisplayPrefsExpanded((prev) => !prev)}
          >
            <DisplayPreferences />
          </AccordionSection>
        </div>
      </div>

      {/* PERFORMANCE */}
      <div>
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-orange)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.settings.groupPerformance')}
          </h3>
        </div>

        <div className="space-y-4">
          <AccordionSection
            title={t('management.sections.settings.performanceOptimizations')}
            description={t('management.sections.settings.performanceOptimizationsDesc')}
            icon={Gauge}
            iconColor="var(--theme-icon-orange)"
            isExpanded={performanceExpanded}
            onToggle={() => setPerformanceExpanded((prev) => !prev)}
          >
            {optimizationsEnabled ? (
              <Suspense
                fallback={
                  <div className="flex items-center justify-center py-8">
                    <div className="text-themed-muted">
                      {t('management.sections.settings.loadingGcSettings')}
                    </div>
                  </div>
                }
              >
                <GcManager isAdmin={isAdmin} />
              </Suspense>
            ) : (
              <Alert color="yellow">
                <div className="min-w-0">
                  <p className="font-medium">
                    {t('management.sections.settings.performanceOptimizationsDisabled')}
                  </p>
                  <p className="text-sm mt-1 mb-2">
                    {t('management.sections.settings.performanceOptimizationsEnvVar')}
                  </p>
                  <pre className="px-3 py-2 rounded text-xs overflow-x-auto break-all whitespace-pre-wrap bg-themed-tertiary">
                    - Optimizations__EnableGarbageCollectionManagement=true
                  </pre>
                </div>
              </Alert>
            )}
          </AccordionSection>
        </div>
      </div>
    </div>
  );
};

export default SettingsSection;
