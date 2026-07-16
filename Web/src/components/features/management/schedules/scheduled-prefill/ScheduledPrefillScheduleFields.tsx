import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { HelpPopover, HelpSection, HelpDefinition, HelpNote } from '@components/ui/HelpPopover';
import { MultiSelectDropdown, type MultiSelectOption } from '@components/ui/MultiSelectDropdown';
import { NumberInput } from '@components/ui/NumberInput';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import ScheduleIntervalPicker from '../ScheduleIntervalPicker';
import { isNotificationMode } from '../types';
import {
  SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS,
  SCHEDULED_PREFILL_OS_OPTIONS,
  SCHEDULED_PREFILL_PRESET_OPTIONS,
  SCHEDULED_PREFILL_SUPPORTED_OPERATING_SYSTEMS,
  SCHEDULED_PREFILL_SUPPORTED_PRESETS
} from './constants';
import type {
  ScheduledPrefillMaxConcurrencyMode,
  ScheduledPrefillOperatingSystem,
  ScheduledPrefillPreset,
  ScheduledPrefillServiceConfigDto,
  ScheduledPrefillServiceKey
} from './types';

interface ScheduledPrefillScheduleFieldsProps {
  serviceKey: ScheduledPrefillServiceKey;
  config: ScheduledPrefillServiceConfigDto;
  disabled?: boolean;
  onChange: (config: ScheduledPrefillServiceConfigDto) => void;
}

/**
 * Sentinel + the 3 real modes, in dropdown order. `'useGlobal'` maps to a `null` DTO override
 * at the onChange boundary - the DTO itself never carries the sentinel string.
 */
const PERSISTENCE_MODE_OVERRIDE_VALUES = [
  'useGlobal',
  'killOnRestart',
  'keepAcrossRestart',
  'fullPersistence'
] as const;

type PersistenceModeOverrideValue = (typeof PERSISTENCE_MODE_OVERRIDE_VALUES)[number];

const isPersistenceModeOverrideValue = (value: string): value is PersistenceModeOverrideValue =>
  (PERSISTENCE_MODE_OVERRIDE_VALUES as readonly string[]).includes(value);

const isScheduledPrefillPreset = (value: string): value is ScheduledPrefillPreset =>
  SCHEDULED_PREFILL_PRESET_OPTIONS.some((option) => option.value === value);

const isScheduledPrefillOperatingSystem = (
  value: string
): value is ScheduledPrefillOperatingSystem =>
  SCHEDULED_PREFILL_OS_OPTIONS.some((option) => option.value === value);

const isScheduledPrefillMaxConcurrencyMode = (
  value: string
): value is ScheduledPrefillMaxConcurrencyMode => value === 'Auto' || value === 'Fixed';

export function ScheduledPrefillScheduleFields({
  serviceKey,
  config,
  disabled = false,
  onChange
}: ScheduledPrefillScheduleFieldsProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const fixedConcurrency =
    config.maxConcurrency.mode === 'Fixed'
      ? config.maxConcurrency.value
      : SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min;

  const presetOptions = useMemo(
    () =>
      SCHEDULED_PREFILL_PRESET_OPTIONS.filter((option) =>
        SCHEDULED_PREFILL_SUPPORTED_PRESETS[serviceKey].includes(option.value)
      ).map((option) => ({
        value: option.value,
        label: t(option.labelKey)
      })),
    [t, serviceKey]
  );

  const presetHelpItems = useMemo(
    () =>
      SCHEDULED_PREFILL_PRESET_OPTIONS.filter((option) =>
        SCHEDULED_PREFILL_SUPPORTED_PRESETS[serviceKey].includes(option.value)
      ).map((option) => ({
        term: t(option.labelKey),
        description: t(option.helpKey)
      })),
    [t, serviceKey]
  );

  const operatingSystemOptions = useMemo<MultiSelectOption[]>(
    () =>
      SCHEDULED_PREFILL_OS_OPTIONS.filter((option) =>
        SCHEDULED_PREFILL_SUPPORTED_OPERATING_SYSTEMS[serviceKey].includes(option.value)
      ).map((option) => ({
        value: option.value,
        label: t(option.labelKey)
      })),
    [t, serviceKey]
  );

  const updateConfig = (patch: Partial<ScheduledPrefillServiceConfigDto>) => {
    onChange({ ...config, ...patch });
  };

  const presetOverridden = config.selectedAppIds.length > 0;

  const intervalLabelId = `scheduled-prefill-interval-label-${serviceKey}`;
  const presetLabelId = `scheduled-prefill-preset-label-${serviceKey}`;
  const osLabelId = `scheduled-prefill-os-label-${serviceKey}`;
  const forceLabelId = `scheduled-prefill-force-label-${serviceKey}`;
  const concurrencyModeLabelId = `scheduled-prefill-concurrency-mode-label-${serviceKey}`;
  const persistenceLabelId = `scheduled-prefill-persistence-label-${serviceKey}`;
  const notificationsLabelId = `scheduled-prefill-notifications-label-${serviceKey}`;

  /* Rows are grouped by control shape - dropdowns, then segmented controls, then the toggle
     pill - so the control column reads as three calm runs instead of alternating shapes. The
     one deliberate exception: a conditional count input (Top count, Fixed connection count)
     stays immediately under the segmented control that reveals it, since separating a child
     input from its parent control would be more confusing than the shape break. */
  return (
    <>
      <div
        className="scheduled-prefill-config-modal__setting-row"
        role="group"
        aria-labelledby={intervalLabelId}
      >
        <div className="scheduled-prefill-config-modal__setting-copy">
          <span id={intervalLabelId} className="scheduled-prefill-config-modal__global-label">
            {t(`${baseKey}.fields.interval`)}
          </span>
          <p className="scheduled-prefill-config-modal__global-help">
            {t(`${baseKey}.fields.intervalHelp`)}
          </p>
        </div>
        <div className="scheduled-prefill-config-modal__setting-actions">
          <ScheduleIntervalPicker
            intervalHours={config.intervalHours}
            isDisabled={disabled}
            onChange={(hours) => updateConfig({ intervalHours: hours })}
          />
        </div>
      </div>

      {SCHEDULED_PREFILL_SUPPORTED_OPERATING_SYSTEMS[serviceKey].length > 0 && (
        <div
          className="scheduled-prefill-config-modal__setting-row"
          role="group"
          aria-labelledby={osLabelId}
        >
          <div className="scheduled-prefill-config-modal__setting-copy">
            <span id={osLabelId} className="scheduled-prefill-config-modal__global-label">
              {t(`${baseKey}.fields.operatingSystems`)}
            </span>
          </div>
          <div className="scheduled-prefill-config-modal__setting-actions">
            <MultiSelectDropdown
              className="scheduled-prefill-setting-fill"
              options={operatingSystemOptions}
              values={config.operatingSystems}
              onChange={(values) =>
                updateConfig({ operatingSystems: values.filter(isScheduledPrefillOperatingSystem) })
              }
              disabled={disabled}
              minSelections={0}
              placeholder={t(`${baseKey}.fields.operatingSystems`)}
            />
          </div>
        </div>
      )}

      <div
        className="scheduled-prefill-config-modal__setting-row"
        role="group"
        aria-labelledby={persistenceLabelId}
      >
        <div className="scheduled-prefill-config-modal__setting-copy">
          <span id={persistenceLabelId} className="scheduled-prefill-config-modal__global-label">
            {t(`${baseKey}.fields.persistenceModeOverride`)}
          </span>
          <p className="scheduled-prefill-config-modal__global-help">
            {t(`${baseKey}.fields.persistenceModeOverrideHelp`)}
          </p>
        </div>
        <div className="scheduled-prefill-config-modal__setting-actions">
          <EnhancedDropdown
            className="scheduled-prefill-setting-fill"
            options={PERSISTENCE_MODE_OVERRIDE_VALUES.map((value) => ({
              value,
              label: t(`${baseKey}.settings.persistenceMode.${value}`)
            }))}
            value={config.persistenceMode ?? 'useGlobal'}
            onChange={(value) => {
              if (!isPersistenceModeOverrideValue(value)) return;
              updateConfig({ persistenceMode: value === 'useGlobal' ? null : value });
            }}
            disabled={disabled}
            variant="button"
            triggerAriaLabel={t(`${baseKey}.fields.persistenceModeOverride`)}
          />
        </div>
      </div>

      <div
        className="scheduled-prefill-config-modal__setting-row"
        role="group"
        aria-labelledby={presetLabelId}
      >
        <div className="scheduled-prefill-config-modal__setting-copy">
          <div className="flex items-center gap-1.5">
            <span id={presetLabelId} className="scheduled-prefill-config-modal__global-label">
              {t(`${baseKey}.fields.preset`)}
            </span>
            <HelpPopover position="left" width={320}>
              <HelpSection title={t(`${baseKey}.presetHelp.title`)} variant="subtle">
                <HelpDefinition items={presetHelpItems} />
                <HelpNote type="warning">{t(`${baseKey}.presetHelp.overrideNote`)}</HelpNote>
              </HelpSection>
            </HelpPopover>
          </div>
          {presetOverridden && (
            <p className="scheduled-prefill-schedule-fields__override">
              {t(`${baseKey}.selectedGames.overridePreset`)}
            </p>
          )}
        </div>
        <div className="scheduled-prefill-config-modal__setting-actions">
          <SegmentedControl
            className="scheduled-prefill-segment-uniform"
            options={presetOptions.map((option) => ({
              ...option,
              disabled: disabled || presetOverridden,
              activeColor: presetOverridden ? 'warning' : 'primary'
            }))}
            value={config.preset}
            onChange={(value) => {
              if (!isScheduledPrefillPreset(value)) return;
              updateConfig({
                preset: value,
                topCount: value === 'Top' ? (config.topCount ?? 50) : null
              });
            }}
            showLabels
          />
        </div>
      </div>

      {config.preset === 'Top' &&
        SCHEDULED_PREFILL_SUPPORTED_PRESETS[serviceKey].includes('Top') && (
          <div className="scheduled-prefill-config-modal__setting-row">
            <div className="scheduled-prefill-config-modal__setting-copy">
              <label
                className="scheduled-prefill-config-modal__global-label"
                htmlFor={`scheduled-prefill-top-count-${serviceKey}`}
              >
                {t(`${baseKey}.fields.topCount`)}
              </label>
            </div>
            <div className="scheduled-prefill-config-modal__setting-actions">
              <NumberInput
                id={`scheduled-prefill-top-count-${serviceKey}`}
                className="scheduled-prefill-number-cap scheduled-prefill-number-cap--full"
                min={1}
                max={99999}
                step={1}
                value={config.topCount ?? 50}
                disabled={disabled || presetOverridden}
                aria-label={t(`${baseKey}.fields.topCount`)}
                onChange={(value) => updateConfig({ topCount: Math.max(1, value) })}
              />
            </div>
          </div>
        )}

      <div
        className="scheduled-prefill-config-modal__setting-row"
        role="group"
        aria-labelledby={concurrencyModeLabelId}
      >
        <div className="scheduled-prefill-config-modal__setting-copy">
          <span
            id={concurrencyModeLabelId}
            className="scheduled-prefill-config-modal__global-label"
          >
            {t(`${baseKey}.fields.maxConcurrency`)}
          </span>
        </div>
        <div className="scheduled-prefill-config-modal__setting-actions">
          <SegmentedControl
            className="scheduled-prefill-segment-uniform"
            options={[
              { value: 'Auto', label: t(`${baseKey}.maxConcurrency.auto`), disabled },
              { value: 'Fixed', label: t(`${baseKey}.maxConcurrency.fixed`), disabled }
            ]}
            value={config.maxConcurrency.mode}
            onChange={(value) => {
              if (!isScheduledPrefillMaxConcurrencyMode(value)) return;
              updateConfig({
                maxConcurrency:
                  value === 'Auto'
                    ? { mode: 'Auto', value: null }
                    : { mode: 'Fixed', value: fixedConcurrency }
              });
            }}
            showLabels
          />
        </div>
      </div>

      {config.maxConcurrency.mode === 'Fixed' && (
        <div className="scheduled-prefill-config-modal__setting-row">
          <div className="scheduled-prefill-config-modal__setting-copy">
            <label
              className="scheduled-prefill-config-modal__global-label"
              htmlFor={`scheduled-prefill-concurrency-${serviceKey}`}
            >
              {t(`${baseKey}.fields.maxConcurrencyValue`)}
            </label>
          </div>
          <div className="scheduled-prefill-config-modal__setting-actions">
            <NumberInput
              id={`scheduled-prefill-concurrency-${serviceKey}`}
              className="scheduled-prefill-number-cap scheduled-prefill-number-cap--full"
              min={SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min}
              max={SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.max}
              step={1}
              value={fixedConcurrency}
              disabled={disabled}
              aria-label={t(`${baseKey}.fields.maxConcurrencyValue`)}
              onChange={(value) =>
                updateConfig({
                  maxConcurrency: {
                    mode: 'Fixed',
                    value: Math.min(
                      SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.max,
                      Math.max(SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min, value)
                    )
                  }
                })
              }
            />
          </div>
        </div>
      )}

      <div
        className="scheduled-prefill-config-modal__setting-row"
        role="group"
        aria-labelledby={notificationsLabelId}
      >
        <div className="scheduled-prefill-config-modal__setting-copy">
          <span id={notificationsLabelId} className="scheduled-prefill-config-modal__global-label">
            {t(`${baseKey}.fields.notifications`)}
          </span>
          <p className="scheduled-prefill-config-modal__global-help">
            {t(`${baseKey}.fields.notificationsHelp`)}
          </p>
        </div>
        <div className="scheduled-prefill-config-modal__setting-actions">
          <SegmentedControl
            className="scheduled-prefill-segment-uniform"
            options={[
              { value: 'all', label: t('management.schedules.notificationMode.all'), disabled },
              {
                value: 'manual',
                label: t('management.schedules.notificationMode.manual'),
                disabled
              },
              {
                value: 'silent',
                label: t('management.schedules.notificationMode.silent'),
                disabled
              }
            ]}
            value={config.notificationMode ?? 'all'}
            onChange={(value) => {
              if (!isNotificationMode(value)) return;
              updateConfig({ notificationMode: value });
            }}
            showLabels
          />
        </div>
      </div>

      <div
        className="scheduled-prefill-config-modal__setting-row"
        role="group"
        aria-labelledby={forceLabelId}
      >
        <div className="scheduled-prefill-config-modal__setting-copy">
          <span id={forceLabelId} className="scheduled-prefill-config-modal__global-label">
            {t(`${baseKey}.fields.force`)}
          </span>
          <p className="scheduled-prefill-config-modal__global-help">
            {t(`${baseKey}.actions.forceDownload`)}
          </p>
        </div>
        <div className="scheduled-prefill-config-modal__setting-actions">
          <ToggleSwitch
            options={[
              { value: 'false', label: t(`${baseKey}.fields.toggleOff`), activeColor: 'default' },
              { value: 'true', label: t(`${baseKey}.fields.toggleOn`), activeColor: 'warning' }
            ]}
            value={config.force ? 'true' : 'false'}
            onChange={(value) => updateConfig({ force: value === 'true' })}
            disabled={disabled}
            title={t(`${baseKey}.fields.force`)}
          />
        </div>
      </div>
    </>
  );
}
