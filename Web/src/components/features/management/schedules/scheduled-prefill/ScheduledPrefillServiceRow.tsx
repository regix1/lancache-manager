import type { ChangeEvent } from 'react';
import { useTranslation } from 'react-i18next';
import {
  SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS,
  SCHEDULED_PREFILL_OS_OPTIONS,
  SCHEDULED_PREFILL_PRESET_OPTIONS
} from './constants';
import type {
  ScheduledPrefillMaxConcurrencyMode,
  ScheduledPrefillOperatingSystem,
  ScheduledPrefillPreset,
  ScheduledPrefillServiceConfigDto,
  ScheduledPrefillServiceKey
} from './types';

interface ScheduledPrefillServiceRowProps {
  serviceKey: ScheduledPrefillServiceKey;
  config: ScheduledPrefillServiceConfigDto;
  disabled?: boolean;
  onChange: (config: ScheduledPrefillServiceConfigDto) => void;
}

export function ScheduledPrefillServiceRow({
  serviceKey,
  config,
  disabled = false,
  onChange
}: ScheduledPrefillServiceRowProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const fixedConcurrency =
    config.maxConcurrency.mode === 'Fixed'
      ? config.maxConcurrency.value
      : SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min;

  const updateConfig = (patch: Partial<ScheduledPrefillServiceConfigDto>) => {
    onChange({ ...config, ...patch });
  };

  const handlePresetChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const preset = event.target.value as ScheduledPrefillPreset;
    updateConfig({
      preset,
      topCount: preset === 'Top' ? (config.topCount ?? 50) : null
    });
  };

  const handleOperatingSystemChange = (
    operatingSystem: ScheduledPrefillOperatingSystem,
    checked: boolean
  ) => {
    const operatingSystems = checked
      ? [...config.operatingSystems, operatingSystem]
      : config.operatingSystems.filter((value) => value !== operatingSystem);

    updateConfig({ operatingSystems });
  };

  const handleMaxConcurrencyModeChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const mode = event.target.value as ScheduledPrefillMaxConcurrencyMode;
    updateConfig({
      maxConcurrency:
        mode === 'Auto'
          ? { mode: 'Auto', value: null }
          : {
              mode: 'Fixed',
              value: fixedConcurrency
            }
    });
  };

  const handleFixedConcurrencyChange = (event: ChangeEvent<HTMLInputElement>) => {
    const nextValue = Number(event.target.value);
    updateConfig({
      maxConcurrency: {
        mode: 'Fixed',
        value: Number.isFinite(nextValue)
          ? Math.min(
              SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.max,
              Math.max(SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min, nextValue)
            )
          : SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min
      }
    });
  };

  return (
    <div className="scheduled-prefill-service-row themed-card border border-themed-primary rounded-lg p-4">
      <div className="scheduled-prefill-service-row__header flex items-center justify-between gap-3">
        <div>
          <h4 className="text-sm font-semibold text-themed-primary">
            {t(`${baseKey}.services.${serviceKey}`)}
          </h4>
          <p className="text-xs text-themed-muted">{config.serviceId}</p>
        </div>
        <label className="scheduled-prefill-service-row__toggle flex items-center gap-2 text-sm text-themed-secondary">
          <input
            type="checkbox"
            className="themed-checkbox"
            checked={config.enabled}
            disabled={disabled}
            onChange={(event) => updateConfig({ enabled: event.target.checked })}
          />
          {t(`${baseKey}.fields.enabled`)}
        </label>
      </div>

      <div className="scheduled-prefill-service-row__grid grid gap-3 md:grid-cols-2 mt-4">
        <label className="scheduled-prefill-service-row__field">
          <span className="block text-xs font-medium text-themed-secondary mb-1">
            {t(`${baseKey}.fields.preset`)}
          </span>
          <select
            className="themed-input w-full"
            value={config.preset}
            disabled={disabled}
            onChange={handlePresetChange}
          >
            {SCHEDULED_PREFILL_PRESET_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {t(option.labelKey)}
              </option>
            ))}
          </select>
        </label>

        <label className="scheduled-prefill-service-row__field">
          <span className="block text-xs font-medium text-themed-secondary mb-1">
            {t(`${baseKey}.fields.topCount`)}
          </span>
          <input
            type="number"
            min={1}
            className="themed-input w-full"
            value={config.topCount ?? ''}
            disabled={disabled || config.preset !== 'Top'}
            onChange={(event) =>
              updateConfig({
                topCount: event.target.value === '' ? null : Math.max(1, Number(event.target.value))
              })
            }
          />
        </label>

        <fieldset className="scheduled-prefill-service-row__field">
          <legend className="block text-xs font-medium text-themed-secondary mb-1">
            {t(`${baseKey}.fields.operatingSystems`)}
          </legend>
          <div className="scheduled-prefill-service-row__os flex flex-wrap gap-3">
            {SCHEDULED_PREFILL_OS_OPTIONS.map((option) => (
              <label
                key={option.value}
                className="scheduled-prefill-service-row__os-option flex items-center gap-2 text-sm text-themed-secondary"
              >
                <input
                  type="checkbox"
                  className="themed-checkbox"
                  checked={config.operatingSystems.includes(option.value)}
                  disabled={disabled}
                  onChange={(event) =>
                    handleOperatingSystemChange(option.value, event.target.checked)
                  }
                />
                {t(option.labelKey)}
              </label>
            ))}
          </div>
        </fieldset>

        <label className="scheduled-prefill-service-row__field">
          <span className="block text-xs font-medium text-themed-secondary mb-1">
            {t(`${baseKey}.fields.force`)}
          </span>
          <span className="scheduled-prefill-service-row__force flex items-center gap-2 text-sm text-themed-secondary">
            <input
              type="checkbox"
              className="themed-checkbox"
              checked={config.force}
              disabled={disabled}
              onChange={(event) => updateConfig({ force: event.target.checked })}
            />
            {t(`${baseKey}.actions.forceDownload`)}
          </span>
        </label>

        <label className="scheduled-prefill-service-row__field">
          <span className="block text-xs font-medium text-themed-secondary mb-1">
            {t(`${baseKey}.fields.maxConcurrency`)}
          </span>
          <select
            className="themed-input w-full"
            value={config.maxConcurrency.mode}
            disabled={disabled}
            onChange={handleMaxConcurrencyModeChange}
          >
            <option value="Auto">{t(`${baseKey}.maxConcurrency.auto`)}</option>
            <option value="Fixed">{t(`${baseKey}.maxConcurrency.fixed`)}</option>
          </select>
        </label>

        <label className="scheduled-prefill-service-row__field">
          <span className="block text-xs font-medium text-themed-secondary mb-1">
            {t(`${baseKey}.fields.maxConcurrencyValue`)}
          </span>
          <input
            type="number"
            min={SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min}
            max={SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.max}
            className="themed-input w-full"
            value={fixedConcurrency}
            disabled={disabled || config.maxConcurrency.mode !== 'Fixed'}
            onChange={handleFixedConcurrencyChange}
          />
        </label>
      </div>
    </div>
  );
}
