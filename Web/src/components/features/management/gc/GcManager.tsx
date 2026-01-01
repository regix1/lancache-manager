import React, { useState, use } from 'react';
import { Cpu, Save, Play, Loader2, Gauge, HardDrive } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { API_BASE } from '@utils/constants';
import authService from '@services/auth.service';

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
  { value: 'disabled', label: 'Disabled', description: 'Memory cleaned by .NET runtime only' },
  { value: 'onpageload', label: 'On Page Load', description: 'Recommended for most users' },
  { value: 'every60minutes', label: 'Every 60 Minutes', description: 'Minimal performance impact' },
  { value: 'every60seconds', label: 'Every 60 Seconds', description: 'Low frequency cleanup' },
  { value: 'every30seconds', label: 'Every 30 Seconds', description: 'Balanced management' },
  { value: 'every10seconds', label: 'Every 10 Seconds', description: 'Frequent cleanup' },
  { value: 'every5seconds', label: 'Every 5 Seconds', description: 'Aggressive cleanup' },
  { value: 'every1second', label: 'Every 1 Second', description: 'Very aggressive' }
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

// Fetch GC settings
const fetchGcSettings = async (): Promise<GcSettings> => {
  try {
    const response = await fetch(`${API_BASE}/gc/settings`, {
      headers: authService.getAuthHeaders()
    });
    if (response.ok) {
      const data = await response.json();
      return data;
    } else {
      throw new Error('Failed to load GC settings');
    }
  } catch (err) {
    console.error('[GcManager] Failed to load settings:', err);
    return {
      aggressiveness: 'disabled',
      memoryThresholdMB: 4096
    };
  }
};

// Cache promise to avoid refetching on every render
let settingsPromise: Promise<GcSettings> | null = null;

const getSettingsPromise = () => {
  if (!settingsPromise) {
    settingsPromise = fetchGcSettings();
  }
  return settingsPromise;
};

interface SettingSectionProps {
  icon: React.ElementType;
  title: string;
  iconColorVar: string;
  children: React.ReactNode;
}

const SettingSection: React.FC<SettingSectionProps> = ({
  icon: Icon,
  title,
  iconColorVar,
  children
}) => (
  <div
    className="p-4 rounded-lg"
    style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
  >
    <div className="flex items-center gap-2 mb-3 pb-2 border-b" style={{ borderColor: 'var(--theme-border-secondary)' }}>
      <div
        className="w-6 h-6 rounded flex items-center justify-center"
        style={{ backgroundColor: `color-mix(in srgb, var(${iconColorVar}) 15%, transparent)` }}
      >
        <Icon className="w-3.5 h-3.5" style={{ color: `var(${iconColorVar})` }} />
      </div>
      <h4 className="text-sm font-semibold" style={{ color: 'var(--theme-text-secondary)' }}>{title}</h4>
    </div>
    <div className="space-y-3">{children}</div>
  </div>
);

interface SettingRowProps {
  label: string;
  description?: string;
  children: React.ReactNode;
}

const SettingRow: React.FC<SettingRowProps> = ({ label, description, children }) => (
  <div className="space-y-2">
    <div>
      <p className="text-sm font-medium" style={{ color: 'var(--theme-text-primary)' }}>{label}</p>
      {description && (
        <p className="text-xs mt-0.5" style={{ color: 'var(--theme-text-muted)' }}>{description}</p>
      )}
    </div>
    {children}
  </div>
);

const GcManager: React.FC<GcManagerProps> = ({ isAuthenticated }) => {
  const initialSettings = use(getSettingsPromise());

  const [settings, setSettings] = useState<GcSettings>(initialSettings);
  const [saving, setSaving] = useState(false);
  const [hasChanges, setHasChanges] = useState(false);
  const [triggering, setTriggering] = useState(false);
  const [triggerResult, setTriggerResult] = useState<GcTriggerResult | null>(null);

  const showToast = (type: 'success' | 'error' | 'info', message: string) => {
    window.dispatchEvent(new CustomEvent('show-toast', {
      detail: { type, message, duration: 4000 }
    }));
  };

  const saveSettings = async () => {
    setSaving(true);
    try {
      const response = await fetch(`${API_BASE}/gc/settings`, {
        method: 'PUT',
        headers: {
          ...authService.getAuthHeaders(),
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(settings)
      });

      if (response.ok) {
        const data = await response.json();
        setSettings(data);
        showToast('success', 'GC settings saved successfully');
        setHasChanges(false);
      } else {
        const errorData = await response.json();
        throw new Error(errorData.error || 'Failed to save GC settings');
      }
    } catch (err) {
      showToast('error', err instanceof Error ? err.message : 'Failed to save GC settings');
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
      showToast('error', err instanceof Error ? err.message : 'Failed to trigger garbage collection');
    } finally {
      setTriggering(false);
    }
  };

  return (
    <div className="space-y-4 pb-32">
      {/* Trigger Result Alert */}
      {triggerResult && (
        <Alert color={triggerResult.skipped ? 'yellow' : 'green'}>
          <div className="text-sm">
            <p className="font-medium">{triggerResult.message}</p>
            {!triggerResult.skipped && triggerResult.beforeMB !== undefined && (
              <div className="mt-1 flex gap-4 text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                <span>Before: {triggerResult.beforeMB} MB</span>
                <span>After: {triggerResult.afterMB} MB</span>
                <span className="font-medium" style={{ color: 'var(--theme-text-primary)' }}>
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

      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {/* Collection Frequency */}
        <SettingSection
          icon={Gauge}
          title="Collection Frequency"
          iconColorVar="--theme-icon-green"
        >
          <SettingRow
            label="Aggressiveness"
            description="How often the system checks and cleans memory"
          >
            <EnhancedDropdown
              options={aggressivenessOptions}
              value={settings.aggressiveness}
              onChange={handleAggressivenessChange}
              disabled={!isAuthenticated || saving}
              className="w-full"
            />
          </SettingRow>
        </SettingSection>

        {/* Memory Settings */}
        <SettingSection
          icon={HardDrive}
          title="Memory Settings"
          iconColorVar="--theme-icon-blue"
        >
          <SettingRow
            label="Threshold"
            description="Memory limit that triggers cleanup when exceeded"
          >
            <EnhancedDropdown
              options={memoryThresholdOptions}
              value={settings.memoryThresholdMB.toString()}
              onChange={handleMemoryThresholdChange}
              disabled={!isAuthenticated || saving}
              className="w-full"
            />
          </SettingRow>
        </SettingSection>
      </div>

      {/* Actions */}
      <div
        className="p-4 rounded-lg"
        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
      >
        <div className="flex items-center gap-2 mb-3 pb-2 border-b" style={{ borderColor: 'var(--theme-border-secondary)' }}>
          <div
            className="w-6 h-6 rounded flex items-center justify-center"
            style={{ backgroundColor: `color-mix(in srgb, var(--theme-icon-orange) 15%, transparent)` }}
          >
            <Cpu className="w-3.5 h-3.5" style={{ color: 'var(--theme-icon-orange)' }} />
          </div>
          <h4 className="text-sm font-semibold" style={{ color: 'var(--theme-text-secondary)' }}>Actions</h4>
        </div>
        <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-3">
          <Button
            onClick={saveSettings}
            disabled={!isAuthenticated || saving || !hasChanges}
            variant="filled"
            color="blue"
            size="sm"
            leftSection={saving ? <Loader2 className="w-4 h-4 animate-spin" /> : <Save className="w-4 h-4" />}
            className="flex-1"
          >
            {saving ? 'Saving...' : 'Save Settings'}
          </Button>
          <Button
            onClick={triggerGarbageCollection}
            disabled={!isAuthenticated || triggering}
            variant="default"
            size="sm"
            leftSection={triggering ? <Loader2 className="w-4 h-4 animate-spin" /> : <Play className="w-4 h-4" />}
            className="flex-1 sm:flex-none"
            title="Manually run garbage collection once (5s cooldown)"
          >
            {triggering ? 'Running...' : 'Run GC Now'}
          </Button>
        </div>
        <p className="text-xs mt-3" style={{ color: 'var(--theme-text-muted)' }}>
          Changes take effect immediately — no restart required.
        </p>
      </div>
    </div>
  );
};

export default GcManager;
