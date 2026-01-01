import React, { useCallback } from 'react';
import {
  Shield,
  Sparkles,
  Settings,
  ToggleLeft,
  ToggleRight
} from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { useMockMode } from '@contexts/MockModeContext';
import { useNotifications } from '@contexts/NotificationsContext';
import AuthenticationManager from '../steam/AuthenticationManager';
import DisplayPreferences from './DisplayPreferences';

interface SettingsSectionProps {
  onApiKeyRegenerated?: () => void;
}

const SettingsSection: React.FC<SettingsSectionProps> = ({ onApiKeyRegenerated }) => {
  const { mockMode, setMockMode } = useMockMode();
  const { addNotification } = useNotifications();

  // Error/Success handlers for AuthenticationManager
  const handleError = useCallback((message: string) => {
    addNotification({
      type: 'generic',
      status: 'failed',
      message,
      details: { notificationType: 'error' }
    });
  }, [addNotification]);

  const handleSuccess = useCallback((message: string) => {
    addNotification({
      type: 'generic',
      status: 'completed',
      message,
      details: { notificationType: 'success' }
    });
  }, [addNotification]);

  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-settings"
      aria-labelledby="tab-settings"
    >
      {/* Section Header */}
      <div className="mb-6">
        <h2 className="text-xl font-semibold text-themed-primary mb-1">
          Settings
        </h2>
        <p className="text-themed-secondary text-sm">
          Manage authentication, demo mode, and display preferences
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
              <h3 className="text-lg font-semibold text-themed-primary">API Authentication</h3>
              <p className="text-xs text-themed-muted">Secure access to management features</p>
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
              <h3 className="text-lg font-semibold text-themed-primary">Demo Mode</h3>
              <p className="text-xs text-themed-muted">Test the interface with simulated data</p>
            </div>
          </div>
          <div
            className="p-4 rounded-lg"
            style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
          >
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
              <div className="flex-1">
                <p className="text-themed-primary text-sm font-medium">Mock Data</p>
                <p className="text-xs text-themed-muted mt-1">
                  Simulates realistic cache data and download activity
                </p>
              </div>
              <Button
                onClick={() => setMockMode(!mockMode)}
                variant={mockMode ? 'filled' : 'outline'}
                color={mockMode ? 'blue' : undefined}
                leftSection={
                  mockMode ? <ToggleRight className="w-4 h-4" /> : <ToggleLeft className="w-4 h-4" />
                }
                className="w-full sm:w-36"
              >
                {mockMode ? 'Enabled' : 'Disabled'}
              </Button>
            </div>
          </div>
          {mockMode && (
            <div className="mt-4">
              <Alert color="blue">
                <span className="text-sm">Mock mode active — API actions are disabled</span>
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
              <h3 className="text-lg font-semibold text-themed-primary">Display Preferences</h3>
              <p className="text-xs text-themed-muted">Customize your experience</p>
            </div>
          </div>
          <DisplayPreferences />
        </Card>
      </div>
    </div>
  );
};

export default SettingsSection;
