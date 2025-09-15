import React, { useEffect, useState } from 'react';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import { Activity, Shield, AlertCircle } from 'lucide-react';

interface TelemetryStatusProps {
  mockMode?: boolean;
}

export const TelemetryStatus: React.FC<TelemetryStatusProps> = ({ mockMode }) => {
  const [telemetryStatus, setTelemetryStatus] = useState<{
    enabled: boolean;
    version?: string;
    privacy_policy?: string;
    loading: boolean;
    error?: string;
  }>({
    enabled: false,
    loading: true
  });

  useEffect(() => {
    fetchTelemetryStatus();
  }, []);

  const fetchTelemetryStatus = async () => {
    if (mockMode) {
      setTelemetryStatus({
        enabled: false,
        version: '1.0.0',
        privacy_policy: 'https://github.com/Regix1/lancache-manager/blob/main/PRIVACY.md',
        loading: false
      });
      return;
    }

    try {
      const response = await fetch('/api/system/telemetry-status');
      const data = await response.json();
      setTelemetryStatus({
        ...data,
        loading: false
      });
    } catch (error) {
      setTelemetryStatus({
        enabled: false,
        loading: false,
        error: 'Failed to fetch telemetry status'
      });
    }
  };

  if (telemetryStatus.loading) {
    return null;
  }

  return (
    <Card title="Privacy & Analytics" icon={<Shield className="w-5 h-5" />}>
      <div className="space-y-4">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <Activity className={`w-5 h-5 ${telemetryStatus.enabled ? 'text-green-500' : 'text-gray-400'}`} />
            <div>
              <p className="font-medium text-themed-primary">
                Anonymous Telemetry
              </p>
              <p className="text-sm text-themed-muted">
                {telemetryStatus.enabled
                  ? 'Helping improve the application'
                  : 'Not collecting any usage data'}
              </p>
            </div>
          </div>
          <div className={`px-3 py-1 rounded-full text-xs font-medium ${
            telemetryStatus.enabled
              ? 'bg-green-500/20 text-green-500'
              : 'bg-gray-500/20 text-gray-400'
          }`}>
            {telemetryStatus.enabled ? 'Enabled' : 'Disabled'}
          </div>
        </div>

        {telemetryStatus.enabled ? (
          <Alert color="blue">
            <div className="space-y-2">
              <p className="text-sm font-medium">Thank you for helping improve LanCache Manager!</p>
              <p className="text-xs text-themed-muted">
                We collect anonymous usage statistics to understand how the app is used and identify common issues.
                No personal information, IP addresses, or cache content is ever collected.
              </p>
              {telemetryStatus.privacy_policy && (
                <a
                  href={telemetryStatus.privacy_policy}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="text-xs text-blue-500 hover:underline"
                >
                  View Privacy Policy →
                </a>
              )}
            </div>
          </Alert>
        ) : (
          <Alert color="yellow">
            <div className="space-y-2">
              <p className="text-sm">
                Anonymous telemetry is currently disabled.
              </p>
              <p className="text-xs text-themed-muted">
                To help improve the application, you can enable telemetry by setting
                <code className="mx-1 px-1 py-0.5 bg-themed-tertiary rounded">TELEMETRY_ENABLED=true</code>
                in your docker-compose.yml file.
              </p>
            </div>
          </Alert>
        )}

        <div className="text-xs text-themed-muted space-y-1">
          <p>• No personal data is collected</p>
          <p>• No IP addresses or identifying information</p>
          <p>• No cache content or file names</p>
          <p>• Only feature usage and error reports</p>
          <p>• Fully anonymous with random IDs</p>
        </div>
      </div>
    </Card>
  );
};