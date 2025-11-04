import React, { useState, useEffect } from 'react';
import { Cpu, Save, RefreshCw, Info, Play, Loader2 } from 'lucide-react';
import { Alert } from '../ui/Alert';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { EnhancedDropdown, type DropdownOption } from '../ui/EnhancedDropdown';
import { API_BASE } from '../../utils/constants';
import authService from '../../services/auth.service';

interface GcSettings {
  aggressiveness: string;
  memoryThresholdMB: number;
}

interface GcTriggerResult {
  skipped: boolean;
  reason?: string;
  remainingSeconds?: number;
  beforeMB?: number;
  afterMB?: number;
  freedMB?: number;
  message: string;
}

interface GcManagerProps {
  isAuthenticated: boolean;
}

const aggressivenessOptions: DropdownOption[] = [
  { value: 'disabled', label: 'Disabled' },
  { value: 'onpageload', label: 'On Page Load Only' },
  { value: 'every60minutes', label: 'Every 60 Minutes' },
  { value: 'every60seconds', label: 'Every 60 Seconds' },
  { value: 'every30seconds', label: 'Every 30 Seconds' },
  { value: 'every10seconds', label: 'Every 10 Seconds' },
  { value: 'every5seconds', label: 'Every 5 Seconds' },
  { value: 'every1second', label: 'Every 1 Second' }
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
    aggressiveness: 'disabled',
    memoryThresholdMB: 4096
  });
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [hasChanges, setHasChanges] = useState(false);
  const [triggering, setTriggering] = useState(false);
  const [triggerResult, setTriggerResult] = useState<GcTriggerResult | null>(null);

  useEffect(() => {
    loadSettings();
  }, []);

  const loadSettings = async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await fetch(`${API_BASE}/gc/settings`, {
        headers: authService.getAuthHeaders()
      });
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
          ...authService.getAuthHeaders(),
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
    setSettings((prev) => ({ ...prev, aggressiveness: value }));
    setHasChanges(true);
  };

  const handleMemoryThresholdChange = (value: string) => {
    setSettings((prev) => ({ ...prev, memoryThresholdMB: parseInt(value) }));
    setHasChanges(true);
  };

  const triggerGarbageCollection = async () => {
    setTriggering(true);
    setError(null);
    setTriggerResult(null);
    try {
      const response = await fetch(`${API_BASE}/gc/trigger`, {
        method: 'POST',
        headers: authService.getAuthHeaders()
      });

      if (response.ok) {
        const data: GcTriggerResult = await response.json();
        setTriggerResult(data);
        setTimeout(() => setTriggerResult(null), 10000);
      } else {
        const errorData = await response.json();
        throw new Error(errorData.error || 'Failed to trigger garbage collection');
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to trigger garbage collection');
    } finally {
      setTriggering(false);
    }
  };

  const getAggressivenessDescription = (level: string): string => {
    switch (level) {
      case 'disabled':
        return 'Garbage collection is disabled. Memory will only be cleaned up by the .NET runtime. May use more memory.';
      case 'onpageload':
        return 'Checks memory on page load/refresh. Only runs GC if memory exceeds threshold. 5-second cooldown prevents spam. Recommended for most users.';
      case 'every60minutes':
        return 'Checks memory every 60 minutes. Only runs GC if threshold exceeded. Minimal performance impact.';
      case 'every60seconds':
        return 'Checks memory every 60 seconds. Only runs GC if threshold exceeded. Low frequency cleanup with minimal performance impact.';
      case 'every30seconds':
        return 'Checks memory every 30 seconds. Only runs GC if threshold exceeded. Moderate cleanup frequency for balanced memory management.';
      case 'every10seconds':
        return 'Checks memory every 10 seconds. Only runs GC if threshold exceeded. More frequent cleanup, slight performance impact.';
      case 'every5seconds':
        return 'Checks memory every 5 seconds. Only runs GC if threshold exceeded. Aggressive cleanup with noticeable performance impact.';
      case 'every1second':
        return 'Checks memory every 1 second. Only runs GC if threshold exceeded. Very aggressive cleanup with significant performance impact.';
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
          <Loader2 className="w-6 h-6 animate-spin text-themed-primary" />
          <span className="ml-2 text-themed-secondary">Loading GC settings...</span>
        </div>
      </Card>
    );
  }

  return (
    <Card>
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Cpu className="w-6 h-6 text-themed-primary" />
          <h3 className="text-lg font-semibold text-themed-primary">Garbage Collection Settings</h3>
        </div>
        <button
          onClick={loadSettings}
          disabled={saving}
          className="p-2 rounded-lg transition-colors disabled:opacity-50 flex items-center justify-center"
          style={{
            color: 'var(--theme-text-muted)',
            backgroundColor: 'transparent'
          }}
          onMouseEnter={(e) =>
            !saving && (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
          }
          onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
          title="Reset to saved settings"
        >
          <RefreshCw className="w-4 h-4" />
        </button>
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
        <Alert color="blue" className="about-section">
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

        {/* Trigger Result */}
        {triggerResult && (
          <Alert color={triggerResult.skipped ? 'yellow' : 'green'} className="mb-4">
            <div className="text-sm">
              <p className="font-medium">{triggerResult.message}</p>
              {!triggerResult.skipped && triggerResult.beforeMB !== undefined && (
                <div className="mt-1 flex gap-4 text-xs text-themed-muted">
                  <span>Before: {triggerResult.beforeMB} MB</span>
                  <span>After: {triggerResult.afterMB} MB</span>
                  <span className="font-medium text-themed-primary">
                    Freed: {triggerResult.freedMB} MB
                  </span>
                </div>
              )}
              {triggerResult.skipped && triggerResult.remainingSeconds !== undefined && (
                <p className="mt-1 text-xs">
                  Cooldown: {Math.ceil(triggerResult.remainingSeconds)}s remaining
                </p>
              )}
            </div>
          </Alert>
        )}

        {/* Action Buttons */}
        <div className="flex items-center gap-3 pt-2">
          <Button
            onClick={saveSettings}
            disabled={!isAuthenticated || saving || !hasChanges}
            variant="filled"
            color="blue"
            size="sm"
            leftSection={<Save className="w-4 h-4" />}
            className="flex-1"
          >
            {saving ? 'Saving...' : 'Save Settings'}
          </Button>
          <Button
            onClick={triggerGarbageCollection}
            disabled={!isAuthenticated || triggering}
            variant="default"
            size="sm"
            leftSection={<Play className="w-4 h-4" />}
            title="Manually run garbage collection once for testing (5s cooldown)"
          >
            {triggering ? 'Running GC...' : 'Run GC Now'}
          </Button>
        </div>
      </div>
    </Card>
  );
};

export default GcManager;
