import React, { useState, useEffect } from 'react';
import { Cpu, Save, RefreshCw, Info } from 'lucide-react';
import { Alert } from '../ui/Alert';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { EnhancedDropdown, DropdownOption } from '../ui/EnhancedDropdown';
import { API_BASE } from '../../utils/constants';

interface GcSettings {
  aggressiveness: string;
  memoryThresholdMB: number;
}

interface GcManagerProps {
  isAuthenticated: boolean;
}

const aggressivenessOptions: DropdownOption[] = [
  { value: 'onpageload', label: 'On Page Load Only' },
  { value: 'low', label: 'Low (Every 5s)' },
  { value: 'medium', label: 'Medium (Every 2s)' },
  { value: 'high', label: 'High (Every 1s)' },
  { value: 'veryhigh', label: 'Very High (Every 0.5s)' }
];

const memoryThresholdOptions: DropdownOption[] = [
  { value: '2048', label: '2 GB' },
  { value: '3072', label: '3 GB' },
  { value: '4096', label: '4 GB' },
  { value: '5120', label: '5 GB' },
  { value: '6144', label: '6 GB' },
  { value: '8192', label: '8 GB' },
  { value: '10240', label: '10 GB' },
  { value: '12288', label: '12 GB' },
  { value: '16384', label: '16 GB' }
];

const GcManager: React.FC<GcManagerProps> = ({ isAuthenticated }) => {
  const [settings, setSettings] = useState<GcSettings>({
    aggressiveness: 'onpageload',
    memoryThresholdMB: 3072
  });
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [hasChanges, setHasChanges] = useState(false);

  useEffect(() => {
    loadSettings();
  }, []);

  const loadSettings = async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await fetch(`${API_BASE}/gc/settings`);
      if (response.ok) {
        const data = await response.json();
        setSettings(data);
        setHasChanges(false);
      } else {
        throw new Error('Failed to load GC settings');
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load GC settings');
    } finally {
      setLoading(false);
    }
  };

  const saveSettings = async () => {
    setSaving(true);
    setError(null);
    setSuccess(null);
    try {
      const response = await fetch(`${API_BASE}/gc/settings`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(settings)
      });

      if (response.ok) {
        const data = await response.json();
        setSettings(data);
        setSuccess('GC settings saved successfully');
        setHasChanges(false);
        setTimeout(() => setSuccess(null), 3000);
      } else {
        const errorData = await response.json();
        throw new Error(errorData.error || 'Failed to save GC settings');
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save GC settings');
    } finally {
      setSaving(false);
    }
  };

  const handleAggressivenessChange = (value: string) => {
    setSettings(prev => ({ ...prev, aggressiveness: value }));
    setHasChanges(true);
  };

  const handleMemoryThresholdChange = (value: string) => {
    setSettings(prev => ({ ...prev, memoryThresholdMB: parseInt(value) }));
    setHasChanges(true);
  };

  const getAggressivenessDescription = (level: string): string => {
    switch (level) {
      case 'onpageload':
        return 'Only runs when you refresh or load a page. 5-second cooldown prevents spam. Recommended for most users.';
      case 'low':
        return 'Runs garbage collection less frequently. Best for performance, but may use more memory.';
      case 'medium':
        return 'Balanced approach. Good performance with reasonable memory usage.';
      case 'high':
        return 'More aggressive cleanup. Better memory control, slight performance impact.';
      case 'veryhigh':
        return 'Very aggressive cleanup. Maximum memory control, noticeable performance impact.';
      default:
        return '';
    }
  };

  const getMemoryThresholdDescription = (thresholdMB: number): string => {
    const thresholdGB = (thresholdMB / 1024).toFixed(1);
    return `Garbage collection will trigger when process memory exceeds ${thresholdGB} GB`;
  };

  if (loading) {
    return (
      <Card>
        <div className="flex items-center justify-center p-8">
          <RefreshCw className="w-6 h-6 animate-spin text-themed-primary" />
          <span className="ml-2 text-themed-secondary">Loading GC settings...</span>
        </div>
      </Card>
    );
  }

  return (
    <Card>
      <div className="flex items-center gap-2 mb-6">
        <Cpu className="w-6 h-6 text-themed-primary" />
        <h3 className="text-lg font-semibold text-themed-primary">Garbage Collection Settings</h3>
      </div>

      {error && (
        <Alert color="red" className="mb-4">
          <span className="text-sm">{error}</span>
        </Alert>
      )}

      {success && (
        <Alert color="green" className="mb-4">
          <span className="text-sm">{success}</span>
        </Alert>
      )}

      <div className="space-y-6">
        {/* Aggressiveness Setting */}
        <div>
          <label className="block text-sm font-medium text-themed-primary mb-2">
            Collection Aggressiveness
          </label>
          <EnhancedDropdown
            options={aggressivenessOptions}
            value={settings.aggressiveness}
            onChange={handleAggressivenessChange}
            disabled={!isAuthenticated || saving}
            className="w-full"
          />
          <div className="mt-2 flex items-start gap-2">
            <Info className="w-4 h-4 text-themed-muted mt-0.5 flex-shrink-0" />
            <p className="text-sm text-themed-muted">
              {getAggressivenessDescription(settings.aggressiveness)}
            </p>
          </div>
        </div>

        {/* Memory Threshold Setting */}
        <div>
          <label className="block text-sm font-medium text-themed-primary mb-2">
            Memory Threshold
          </label>
          <EnhancedDropdown
            options={memoryThresholdOptions}
            value={settings.memoryThresholdMB.toString()}
            onChange={handleMemoryThresholdChange}
            disabled={!isAuthenticated || saving}
            className="w-full"
          />
          <div className="mt-2 flex items-start gap-2">
            <Info className="w-4 h-4 text-themed-muted mt-0.5 flex-shrink-0" />
            <p className="text-sm text-themed-muted">
              {getMemoryThresholdDescription(settings.memoryThresholdMB)}
            </p>
          </div>
        </div>

        {/* Info Box */}
        <Alert color="blue">
          <div className="text-sm space-y-2">
            <p className="font-medium">About Garbage Collection</p>
            <p>
              These settings control how aggressively the system cleans up memory. If you experience
              high memory usage, try increasing the aggressiveness or lowering the threshold. If
              performance is slow, try decreasing the aggressiveness.
            </p>
            <p className="text-xs text-themed-muted mt-2">
              Note: Changes take effect immediately and don't require a restart.
            </p>
          </div>
        </Alert>

        {/* Action Buttons */}
        <div className="flex gap-3 pt-2">
          <Button
            onClick={saveSettings}
            disabled={!isAuthenticated || saving || !hasChanges}
            variant="filled"
            color="blue"
            leftSection={<Save className="w-4 h-4" />}
            className="flex-1"
          >
            {saving ? 'Saving...' : 'Save Settings'}
          </Button>
          <Button
            onClick={loadSettings}
            disabled={saving}
            variant="outline"
            color="gray"
            leftSection={<RefreshCw className="w-4 h-4" />}
          >
            Reset
          </Button>
        </div>
      </div>
    </Card>
  );
};

export default GcManager;
