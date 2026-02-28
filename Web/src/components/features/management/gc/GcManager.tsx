import React, { useState, useEffect } from 'react';
import { Cpu, Save, Play, Loader2, Gauge, HardDrive } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { useNotifications } from '@contexts/notifications';
import { API_BASE } from '@utils/constants';
import ApiService from '@services/api.service';

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
  isAdmin: boolean;
}

// Fetch GC settings
const fetchGcSettings = async (): Promise<GcSettings> => {
  try {
    const response = await fetch(`${API_BASE}/gc/settings`, ApiService.getFetchOptions());
    if (response.ok) {
      const data = await response.json();
      return data;
    } else if (response.status === 404) {
      // GC feature is disabled on backend, return default settings
      return {
        aggressiveness: 'disabled',
        memoryThresholdMB: 4096
      };
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
  <div className="p-4 rounded-lg bg-themed-tertiary">
    <div className="flex items-center gap-2 mb-3 pb-2 border-b border-themed-secondary">
      <div
        className="w-6 h-6 rounded flex items-center justify-center"
        style={{ backgroundColor: `color-mix(in srgb, var(${iconColorVar}) 15%, transparent)` }}
      >
        <Icon className="w-3.5 h-3.5" style={{ color: `var(${iconColorVar})` }} />
      </div>
      <h4 className="text-sm font-semibold text-themed-secondary">{title}</h4>
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
      <p className="text-sm font-medium text-themed-primary">{label}</p>
      {description && <p className="text-xs mt-0.5 text-themed-muted">{description}</p>}
    </div>
    {children}
  </div>
);

const GcManager: React.FC<GcManagerProps> = ({ isAdmin }) => {
  const { t } = useTranslation();
  const { addNotification } = useNotifications();
  const [loading, setLoading] = useState(true);

  const aggressivenessOptions: DropdownOption[] = [
    {
      value: 'disabled',
      label: t('management.gc.aggressiveness.disabled'),
      description: t('management.gc.aggressiveness.disabledDesc')
    },
    {
      value: 'onpageload',
      label: t('management.gc.aggressiveness.onPageLoad'),
      description: t('management.gc.aggressiveness.onPageLoadDesc')
    },
    {
      value: 'every60minutes',
      label: t('management.gc.aggressiveness.every60min'),
      description: t('management.gc.aggressiveness.every60minDesc')
    },
    {
      value: 'every60seconds',
      label: t('management.gc.aggressiveness.every60sec'),
      description: t('management.gc.aggressiveness.every60secDesc')
    },
    {
      value: 'every30seconds',
      label: t('management.gc.aggressiveness.every30sec'),
      description: t('management.gc.aggressiveness.every30secDesc')
    },
    {
      value: 'every10seconds',
      label: t('management.gc.aggressiveness.every10sec'),
      description: t('management.gc.aggressiveness.every10secDesc')
    },
    {
      value: 'every5seconds',
      label: t('management.gc.aggressiveness.every5sec'),
      description: t('management.gc.aggressiveness.every5secDesc')
    },
    {
      value: 'every1second',
      label: t('management.gc.aggressiveness.every1sec'),
      description: t('management.gc.aggressiveness.every1secDesc')
    }
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

  const [settings, setSettings] = useState<GcSettings>({
    aggressiveness: 'disabled',
    memoryThresholdMB: 4096
  });
  const [saving, setSaving] = useState(false);
  const [hasChanges, setHasChanges] = useState(false);
  const [triggering, setTriggering] = useState(false);
  const [triggerResult, setTriggerResult] = useState<GcTriggerResult | null>(null);

  // Load settings on mount
  useEffect(() => {
    const loadSettings = async () => {
      try {
        const data = await fetchGcSettings();
        setSettings(data);
      } finally {
        setLoading(false);
      }
    };
    loadSettings();
  }, []);

  const saveSettings = async () => {
    setSaving(true);
    try {
      const response = await fetch(
        `${API_BASE}/gc/settings`,
        ApiService.getFetchOptions({
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(settings)
        })
      );

      if (response.ok) {
        const data = await response.json();
        setSettings(data);
        addNotification({
          type: 'generic',
          status: 'completed',
          message: t('management.gc.saveSuccess'),
          details: { notificationType: 'success' }
        });
        setHasChanges(false);
      } else {
        const errorData = await response.json();
        throw new Error(errorData.error || t('management.gc.saveFailed'));
      }
    } catch (err) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: err instanceof Error ? err.message : t('management.gc.saveFailed'),
        details: { notificationType: 'error' }
      });
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
      const response = await fetch(
        `${API_BASE}/gc/trigger`,
        ApiService.getFetchOptions({
          method: 'POST'
        })
      );

      if (response.ok) {
        const data: GcTriggerResult = await response.json();
        setTriggerResult(data);
        setTimeout(() => setTriggerResult(null), 10000);
      } else {
        const errorData = await response.json();
        throw new Error(errorData.error || t('management.gc.triggerFailed'));
      }
    } catch (err) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: err instanceof Error ? err.message : t('management.gc.triggerFailed'),
        details: { notificationType: 'error' }
      });
    } finally {
      setTriggering(false);
    }
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center p-8">
        <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
      </div>
    );
  }

  return (
    <div className="space-y-4 pb-32">
      {/* Trigger Result Alert */}
      {triggerResult && (
        <Alert color={triggerResult.skipped ? 'yellow' : 'green'}>
          <div className="text-sm">
            <p className="font-medium">{triggerResult.message}</p>
            {!triggerResult.skipped && triggerResult.beforeMB !== undefined && (
              <div className="mt-1 flex gap-4 text-xs text-themed-muted">
                <span>
                  {t('management.gc.before')}: {triggerResult.beforeMB} MB
                </span>
                <span>
                  {t('management.gc.after')}: {triggerResult.afterMB} MB
                </span>
                <span className="font-medium text-themed-primary">
                  {t('management.gc.freed')}: {triggerResult.freedMB} MB
                </span>
              </div>
            )}
            {triggerResult.skipped && triggerResult.remainingSeconds !== undefined && (
              <p className="mt-1 text-xs">
                {t('management.gc.cooldown')}: {Math.ceil(triggerResult.remainingSeconds)}s{' '}
                {t('management.gc.remaining')}
              </p>
            )}
          </div>
        </Alert>
      )}

      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {/* Collection Frequency */}
        <SettingSection
          icon={Gauge}
          title={t('management.gc.collectionFrequency')}
          iconColorVar="--theme-icon-green"
        >
          <SettingRow
            label={t('management.gc.aggressivenessLabel')}
            description={t('management.gc.aggressivenessDesc')}
          >
            <EnhancedDropdown
              options={aggressivenessOptions}
              value={settings.aggressiveness}
              onChange={handleAggressivenessChange}
              disabled={!isAdmin || saving}
              className="w-full"
            />
          </SettingRow>
        </SettingSection>

        {/* Memory Settings */}
        <SettingSection
          icon={HardDrive}
          title={t('management.gc.memorySettings')}
          iconColorVar="--theme-icon-blue"
        >
          <SettingRow
            label={t('management.gc.thresholdLabel')}
            description={t('management.gc.thresholdDesc')}
          >
            <EnhancedDropdown
              options={memoryThresholdOptions}
              value={settings.memoryThresholdMB.toString()}
              onChange={handleMemoryThresholdChange}
              disabled={!isAdmin || saving}
              className="w-full"
            />
          </SettingRow>
        </SettingSection>
      </div>

      {/* Actions */}
      <div className="p-4 rounded-lg bg-themed-tertiary">
        <div className="flex items-center gap-2 mb-3 pb-2 border-b border-themed-secondary">
          <div className="w-6 h-6 rounded flex items-center justify-center icon-bg-orange">
            <Cpu className="w-3.5 h-3.5 icon-orange" />
          </div>
          <h4 className="text-sm font-semibold text-themed-secondary">
            {t('management.gc.actions')}
          </h4>
        </div>
        <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-3">
          <Button
            onClick={saveSettings}
            disabled={!isAdmin || saving || !hasChanges}
            variant="filled"
            color="blue"
            size="sm"
            leftSection={
              saving ? <Loader2 className="w-4 h-4 animate-spin" /> : <Save className="w-4 h-4" />
            }
            className="flex-1"
          >
            {saving ? t('management.gc.saving') : t('management.gc.saveSettings')}
          </Button>
          <Button
            onClick={triggerGarbageCollection}
            disabled={!isAdmin || triggering}
            variant="default"
            size="sm"
            leftSection={
              triggering ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Play className="w-4 h-4" />
              )
            }
            className="flex-1 sm:flex-none"
            title={t('management.gc.runGcTooltip')}
          >
            {triggering ? t('management.gc.running') : t('management.gc.runGcNow')}
          </Button>
        </div>
        <p className="text-xs mt-3 text-themed-muted">{t('management.gc.noRestartRequired')}</p>
      </div>
    </div>
  );
};

export default GcManager;
