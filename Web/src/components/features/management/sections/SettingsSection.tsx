import React, { useCallback, Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { Shield, Sparkles, Settings, ToggleLeft, ToggleRight, Gauge, RotateCw } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { useMockMode } from '@contexts/MockModeContext';
import { useNotifications } from '@contexts/notifications';
import AuthenticationManager from '../steam/AuthenticationManager';
import DisplayPreferences from './DisplayPreferences';
import GcManager from '../gc/GcManager';
import LogRotationManager from '../LogRotationManager';

interface SettingsSectionProps {
  onApiKeyRegenerated?: () => void;
  optimizationsEnabled: boolean;
  logRotationEnabled: boolean;
  isAuthenticated: boolean;
}

const SettingsSection: React.FC<SettingsSectionProps> = ({
  onApiKeyRegenerated,
  optimizationsEnabled,
  logRotationEnabled,
  isAuthenticated
}) => {
  const { t } = useTranslation();
  const { mockMode, setMockMode } = useMockMode();
  const { addNotification } = useNotifications();

  // Error/Success handlers for AuthenticationManager
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
      {/* Section Header */}
      <div className="mb-6">
        <h2 className="text-xl font-semibold text-themed-primary mb-1">{t('management.sections.settings.title')}</h2>
        <p className="text-themed-secondary text-sm">
          {t('management.sections.settings.subtitle')}
        </p>
      </div>

      <div className="space-y-4">
        {/* Authentication Card */}
        <Card>
          <div className="flex items-center gap-3 mb-4">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-green">
              <Shield className="w-5 h-5 icon-green" />
            </div>
            <div>
              <h3 className="text-lg font-semibold text-themed-primary">{t('management.sections.settings.apiAuth')}</h3>
              <p className="text-xs text-themed-muted">{t('management.sections.settings.apiAuthDesc')}</p>
            </div>
          </div>
          <AuthenticationManager
            onError={handleError}
            onSuccess={handleSuccess}
            onApiKeyRegenerated={onApiKeyRegenerated}
          />
        </Card>

        {/* Demo Mode Card */}
        <Card>
          <div className="flex items-center gap-3 mb-4">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-purple">
              <Sparkles className="w-5 h-5 icon-purple" />
            </div>
            <div>
              <h3 className="text-lg font-semibold text-themed-primary">{t('management.sections.settings.demoMode')}</h3>
              <p className="text-xs text-themed-muted">{t('management.sections.settings.demoModeDesc')}</p>
            </div>
          </div>
          <div className="p-4 rounded-lg bg-themed-tertiary">
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
              <div className="flex-1">
                <p className="text-themed-primary text-sm font-medium">{t('management.sections.settings.mockData')}</p>
                <p className="text-xs text-themed-muted mt-1">
                  {t('management.sections.settings.mockDataDesc')}
                </p>
              </div>
              <Button
                onClick={() => setMockMode(!mockMode)}
                variant={mockMode ? 'filled' : 'outline'}
                color={mockMode ? 'blue' : undefined}
                leftSection={
                  mockMode ? (
                    <ToggleRight className="w-4 h-4" />
                  ) : (
                    <ToggleLeft className="w-4 h-4" />
                  )
                }
                className="w-full sm:w-36"
              >
                {mockMode ? t('management.sections.settings.enabled') : t('management.sections.settings.disabled')}
              </Button>
            </div>
          </div>
          {mockMode && (
            <div className="mt-4">
              <Alert color="blue">
                <span className="text-sm">{t('management.sections.settings.mockModeActive')}</span>
              </Alert>
            </div>
          )}
        </Card>

        {/* Display Preferences Card */}
        <Card>
          <div className="flex items-center gap-3 mb-6">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-blue">
              <Settings className="w-5 h-5 icon-blue" />
            </div>
            <div>
              <h3 className="text-lg font-semibold text-themed-primary">{t('management.sections.settings.displayPreferences')}</h3>
              <p className="text-xs text-themed-muted">{t('management.sections.settings.displayPreferencesDesc')}</p>
            </div>
          </div>
          <DisplayPreferences />
        </Card>

        {/* Log Rotation Card */}
        <Card>
          <div className="flex items-center gap-3 mb-4">
            <div
              className={`w-10 h-10 rounded-lg flex items-center justify-center ${logRotationEnabled ? 'icon-bg-cyan' : 'icon-bg-gray'}`}
            >
              <RotateCw className={`w-5 h-5 ${logRotationEnabled ? 'icon-cyan' : 'icon-gray'}`} />
            </div>
            <div>
              <h3 className="text-lg font-semibold text-themed-primary">{t('management.sections.settings.nginxLogRotation')}</h3>
              <p className="text-xs text-themed-muted">
                {t('management.sections.settings.nginxLogRotationDesc')}
              </p>
            </div>
          </div>
          {logRotationEnabled ? (
            <LogRotationManager
              isAuthenticated={isAuthenticated}
              onError={handleError}
              onSuccess={handleSuccess}
            />
          ) : (
            <Alert color="yellow">
              <div className="min-w-0">
                <p className="font-medium">{t('management.sections.settings.nginxLogRotationDisabled')}</p>
                <p className="text-sm mt-1 mb-2">
                  {t('management.sections.settings.nginxLogRotationEnvVar')}
                </p>
                <pre className="px-3 py-2 rounded text-xs overflow-x-auto break-all whitespace-pre-wrap bg-themed-tertiary">
                  - NginxLogRotation__Enabled=true
                </pre>
              </div>
            </Alert>
          )}
        </Card>

        {/* Performance Optimizations Card */}
        <Card>
          <div className="flex items-center gap-3 mb-4">
            <div
              className={`w-10 h-10 rounded-lg flex items-center justify-center ${optimizationsEnabled ? 'icon-bg-orange' : 'icon-bg-gray'}`}
            >
              <Gauge className={`w-5 h-5 ${optimizationsEnabled ? 'icon-orange' : 'icon-gray'}`} />
            </div>
            <div>
              <h3 className="text-lg font-semibold text-themed-primary">
                {t('management.sections.settings.performanceOptimizations')}
              </h3>
              <p className="text-xs text-themed-muted">{t('management.sections.settings.performanceOptimizationsDesc')}</p>
            </div>
          </div>
          {optimizationsEnabled ? (
            <Suspense
              fallback={
                <div className="flex items-center justify-center py-8">
                  <div className="text-themed-muted">{t('management.sections.settings.loadingGcSettings')}</div>
                </div>
              }
            >
              <GcManager isAuthenticated={isAuthenticated} />
            </Suspense>
          ) : (
            <Alert color="yellow">
              <div className="min-w-0">
                <p className="font-medium">{t('management.sections.settings.performanceOptimizationsDisabled')}</p>
                <p className="text-sm mt-1 mb-2">
                  {t('management.sections.settings.performanceOptimizationsEnvVar')}
                </p>
                <pre className="px-3 py-2 rounded text-xs overflow-x-auto break-all whitespace-pre-wrap bg-themed-tertiary">
                  - Optimizations__EnableGarbageCollectionManagement=true
                </pre>
              </div>
            </Alert>
          )}
        </Card>
      </div>
    </div>
  );
};

export default SettingsSection;
