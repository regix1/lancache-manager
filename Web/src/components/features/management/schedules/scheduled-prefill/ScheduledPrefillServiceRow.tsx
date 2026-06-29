import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import Badge from '@components/ui/Badge';
import { MultiSelectDropdown, type MultiSelectOption } from '@components/ui/MultiSelectDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { NumberInput } from '@components/ui/NumberInput';
import {
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS,
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

const isScheduledPrefillPreset = (value: string): value is ScheduledPrefillPreset =>
  SCHEDULED_PREFILL_PRESET_OPTIONS.some((option) => option.value === value);

const isScheduledPrefillOperatingSystem = (
  value: string
): value is ScheduledPrefillOperatingSystem =>
  SCHEDULED_PREFILL_OS_OPTIONS.some((option) => option.value === value);

const isScheduledPrefillMaxConcurrencyMode = (
  value: string
): value is ScheduledPrefillMaxConcurrencyMode => value === 'Auto' || value === 'Fixed';

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
  const [isExpanded, setIsExpanded] = useState(config.enabled);
  const selectedGamesCount = config.selectedAppIds.length;
  const supportsAccountPersistent = (
    SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS as readonly string[]
  ).includes(serviceKey);

  useEffect(() => {
    if (config.enabled) {
      setIsExpanded(true);
    }
  }, [config.enabled]);

  const presetOptions = useMemo(
    () =>
      SCHEDULED_PREFILL_PRESET_OPTIONS.map((option) => ({
        value: option.value,
        label: t(option.labelKey)
      })),
    [t]
  );

  const operatingSystemOptions = useMemo<MultiSelectOption[]>(
    () =>
      SCHEDULED_PREFILL_OS_OPTIONS.map((option) => ({
        value: option.value,
        label: t(option.labelKey)
      })),
    [t]
  );

  const updateConfig = (patch: Partial<ScheduledPrefillServiceConfigDto>) => {
    onChange({ ...config, ...patch });
  };

  const handleEnabledChange = (value: string) => {
    const enabled = value === 'enabled';
    updateConfig({ enabled });
    if (enabled) {
      setIsExpanded(true);
    }
  };

  const handlePresetChange = (value: string) => {
    if (!isScheduledPrefillPreset(value)) {
      return;
    }

    updateConfig({
      preset: value,
      topCount: value === 'Top' ? (config.topCount ?? 50) : null
    });
  };

  const handleOperatingSystemsChange = (values: string[]) => {
    updateConfig({ operatingSystems: values.filter(isScheduledPrefillOperatingSystem) });
  };

  const handleMaxConcurrencyModeChange = (value: string) => {
    if (!isScheduledPrefillMaxConcurrencyMode(value)) {
      return;
    }

    updateConfig({
      maxConcurrency:
        value === 'Auto'
          ? { mode: 'Auto', value: null }
          : {
              mode: 'Fixed',
              value: fixedConcurrency
            }
    });
  };

  const handleTopCountChange = (value: string) => {
    updateConfig({
      topCount: value === '' ? null : Math.max(1, Number(value))
    });
  };

  const handleFixedConcurrencyChange = (value: string) => {
    const nextValue = Number(value);
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
    <AccordionSection
      title={t(`${baseKey}.services.${serviceKey}`)}
      isExpanded={isExpanded}
      onToggle={() => setIsExpanded((current) => !current)}
      badge={
        <div className="scheduled-prefill-service-row__header-actions">
          <Badge variant={config.enabled ? 'success' : 'neutral'}>{config.serviceId}</Badge>
          <ToggleSwitch
            options={[
              {
                value: 'disabled',
                label: t('management.schedules.disabled'),
                activeColor: 'default'
              },
              {
                value: 'enabled',
                label: t(`${baseKey}.fields.enabled`),
                activeColor: 'success'
              }
            ]}
            value={config.enabled ? 'enabled' : 'disabled'}
            onChange={handleEnabledChange}
            disabled={disabled}
            title={t(`${baseKey}.fields.enabled`)}
          />
        </div>
      }
    >
      <div className="scheduled-prefill-service-row">
        <div className="scheduled-prefill-service-row__grid">
          <div className="scheduled-prefill-service-row__field">
            <span className="scheduled-prefill-service-row__label">
              {t(`${baseKey}.fields.preset`)}
            </span>
            <SegmentedControl
              options={presetOptions.map((option) => ({ ...option, disabled }))}
              value={config.preset}
              onChange={handlePresetChange}
              fullWidth
              showLabels
            />
            {supportsAccountPersistent && selectedGamesCount > 0 && (
              <p className="scheduled-prefill-service-row__selected-override">
                {t(`${baseKey}.selectedGames.count`, { count: selectedGamesCount })}
                {' — '}
                {t(`${baseKey}.selectedGames.overridePreset`)}
              </p>
            )}
          </div>

          {config.preset === 'Top' && (
            <div className="scheduled-prefill-service-row__field">
              <label
                className="scheduled-prefill-service-row__label"
                htmlFor={`scheduled-prefill-top-count-${serviceKey}`}
              >
                {t(`${baseKey}.fields.topCount`)}
              </label>
              <NumberInput
                id={`scheduled-prefill-top-count-${serviceKey}`}
                min={1}
                max={99999}
                step={1}
                value={config.topCount ?? 50}
                disabled={disabled}
                aria-label={t(`${baseKey}.fields.topCount`)}
                onChange={(value) => handleTopCountChange(String(value))}
              />
            </div>
          )}

          <div className="scheduled-prefill-service-row__field">
            <span className="scheduled-prefill-service-row__label">
              {t(`${baseKey}.fields.operatingSystems`)}
            </span>
            <MultiSelectDropdown
              options={operatingSystemOptions}
              values={config.operatingSystems}
              onChange={handleOperatingSystemsChange}
              disabled={disabled}
              minSelections={0}
              placeholder={t(`${baseKey}.fields.operatingSystems`)}
            />
          </div>

          <div className="scheduled-prefill-service-row__field">
            <div className="scheduled-prefill-service-row__toggle-row">
              <div>
                <span className="scheduled-prefill-service-row__label">
                  {t(`${baseKey}.fields.force`)}
                </span>
                <p className="scheduled-prefill-service-row__help">
                  {t(`${baseKey}.actions.forceDownload`)}
                </p>
              </div>
              <ToggleSwitch
                options={[
                  {
                    value: 'false',
                    label: t('management.schedules.disabled'),
                    activeColor: 'default'
                  },
                  {
                    value: 'true',
                    label: t(`${baseKey}.fields.enabled`),
                    activeColor: 'warning'
                  }
                ]}
                value={config.force ? 'true' : 'false'}
                onChange={(value) => updateConfig({ force: value === 'true' })}
                disabled={disabled}
                title={t(`${baseKey}.fields.force`)}
              />
            </div>
          </div>

          <div className="scheduled-prefill-service-row__field">
            <span className="scheduled-prefill-service-row__label">
              {t(`${baseKey}.fields.maxConcurrency`)}
            </span>
            <SegmentedControl
              options={[
                {
                  value: 'Auto',
                  label: t(`${baseKey}.maxConcurrency.auto`),
                  disabled
                },
                {
                  value: 'Fixed',
                  label: t(`${baseKey}.maxConcurrency.fixed`),
                  disabled
                }
              ]}
              value={config.maxConcurrency.mode}
              onChange={handleMaxConcurrencyModeChange}
              fullWidth
              showLabels
            />
          </div>

          {config.maxConcurrency.mode === 'Fixed' && (
            <div className="scheduled-prefill-service-row__field">
              <label
                className="scheduled-prefill-service-row__label"
                htmlFor={`scheduled-prefill-concurrency-${serviceKey}`}
              >
                {t(`${baseKey}.fields.maxConcurrencyValue`)}
              </label>
              <NumberInput
                id={`scheduled-prefill-concurrency-${serviceKey}`}
                min={SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min}
                max={SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.max}
                step={1}
                value={fixedConcurrency}
                disabled={disabled}
                aria-label={t(`${baseKey}.fields.maxConcurrencyValue`)}
                onChange={(value) => handleFixedConcurrencyChange(String(value))}
              />
            </div>
          )}
        </div>
      </div>
    </AccordionSection>
  );
}
