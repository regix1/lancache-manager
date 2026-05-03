import React, { useCallback, useEffect, useMemo, useRef, useState, memo } from 'react';
import { useTranslation } from 'react-i18next';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Button } from '@components/ui/Button';
import { getScheduleIntervalOptions } from './constants';
import './ScheduleIntervalPicker.css';

const CUSTOM_SENTINEL = 'custom' as const;
const MIN_CUSTOM_MINUTES = 1;
const MAX_CUSTOM_MINUTES = 59;

interface ScheduleIntervalPickerProps {
  intervalHours: number;
  isDisabled: boolean;
  onChange: (hours: number) => void;
}

function deriveInitialMode(hours: number): { mode: 'preset' | 'custom'; minutes: string } {
  if (hours > 0 && hours < 1) {
    return { mode: 'custom', minutes: String(Math.round(hours * 60)) };
  }
  return { mode: 'preset', minutes: '30' };
}

function formatIntervalLabel(hours: number, t: ReturnType<typeof useTranslation>['t']): string {
  if (hours <= 0) return '';
  if (hours < 1) {
    const count = Math.round(hours * 60);
    return t('management.schedules.everyNMinutes', { count });
  }
  return t('management.schedules.everyNHours', { count: hours });
}

const ScheduleIntervalPicker = memo(function ScheduleIntervalPicker({
  intervalHours,
  isDisabled,
  onChange
}: ScheduleIntervalPickerProps) {
  const { t } = useTranslation();
  const inputRef = useRef<HTMLInputElement>(null);

  const [mode, setMode] = useState<'preset' | 'custom'>(
    () => deriveInitialMode(intervalHours).mode
  );
  const [customMinutes, setCustomMinutes] = useState<string>(
    () => deriveInitialMode(intervalHours).minutes
  );

  useEffect(() => {
    const derived = deriveInitialMode(intervalHours);
    setMode(derived.mode);
    setCustomMinutes(derived.minutes);
  }, [intervalHours]);

  const presetsWithoutCustom = useMemo((): DropdownOption[] => {
    const all = getScheduleIntervalOptions(t);
    return all.filter((opt) => opt.value !== CUSTOM_SENTINEL);
  }, [t]);

  const dropdownOptions = useMemo((): DropdownOption[] => {
    if (mode === 'custom') {
      const trimmed = customMinutes.trim();
      const parsed = Number(trimmed);
      const isValid =
        trimmed.length > 0 &&
        Number.isFinite(parsed) &&
        Number.isInteger(parsed) &&
        parsed >= MIN_CUSTOM_MINUTES &&
        parsed <= MAX_CUSTOM_MINUTES;

      const topOption: DropdownOption = isValid
        ? { value: String(parsed / 60), label: formatIntervalLabel(parsed / 60, t) }
        : { value: CUSTOM_SENTINEL, label: t('management.schedules.intervals.custom') };

      return [topOption, ...presetsWithoutCustom];
    }

    // preset mode
    const standardOptions = getScheduleIntervalOptions(t);
    const currentVal =
      intervalHours === 0 ? '0' : intervalHours === -1 ? '-1' : String(intervalHours);
    const hasCurrentOption = standardOptions.some((opt) => opt.value === currentVal);

    if (!hasCurrentOption && intervalHours > 0) {
      const dynamicOption: DropdownOption = {
        value: currentVal,
        label: formatIntervalLabel(intervalHours, t)
      };
      return [dynamicOption, ...standardOptions];
    }

    return standardOptions;
  }, [mode, customMinutes, intervalHours, presetsWithoutCustom, t]);

  const dropdownValue = useMemo((): string => {
    if (mode === 'custom') {
      const trimmed = customMinutes.trim();
      const parsed = Number(trimmed);
      const isValid =
        trimmed.length > 0 &&
        Number.isFinite(parsed) &&
        Number.isInteger(parsed) &&
        parsed >= MIN_CUSTOM_MINUTES &&
        parsed <= MAX_CUSTOM_MINUTES;
      return isValid ? String(parsed / 60) : CUSTOM_SENTINEL;
    }
    return intervalHours === 0 ? '0' : intervalHours === -1 ? '-1' : String(intervalHours);
  }, [mode, customMinutes, intervalHours]);

  const isCustomInputValid = useMemo((): boolean => {
    const trimmed = customMinutes.trim();
    if (trimmed.length === 0) return false;
    const parsed = Number(trimmed);
    return (
      Number.isFinite(parsed) &&
      Number.isInteger(parsed) &&
      parsed >= MIN_CUSTOM_MINUTES &&
      parsed <= MAX_CUSTOM_MINUTES
    );
  }, [customMinutes]);

  const handleDropdownChange = useCallback(
    (value: string) => {
      if (value === CUSTOM_SENTINEL) {
        setMode('custom');
        requestAnimationFrame(() => {
          if (inputRef.current) {
            inputRef.current.focus();
            inputRef.current.select();
          }
        });
        return;
      }
      const parsed = Number(value);
      if (Number.isFinite(parsed)) {
        setMode('preset');
        onChange(parsed);
      }
    },
    [onChange]
  );

  const handleApply = useCallback(() => {
    if (!isCustomInputValid) return;
    const minutes = Number(customMinutes.trim());
    onChange(minutes / 60);
  }, [isCustomInputValid, customMinutes, onChange]);

  const handleInputKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLInputElement>) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        handleApply();
      }
    },
    [handleApply]
  );

  const handleCustomInputChange = useCallback((event: React.ChangeEvent<HTMLInputElement>) => {
    setCustomMinutes(event.target.value);
  }, []);

  return (
    <div className="schedule-interval-picker">
      <div className="schedule-interval-picker-dropdown">
        <EnhancedDropdown
          options={dropdownOptions}
          value={dropdownValue}
          onChange={handleDropdownChange}
          disabled={isDisabled}
          variant="button"
        />
      </div>
      {mode === 'custom' && (
        <div className="schedule-interval-picker-custom-row">
          <input
            ref={inputRef}
            type="number"
            min={MIN_CUSTOM_MINUTES}
            max={MAX_CUSTOM_MINUTES}
            step={1}
            value={customMinutes}
            onChange={handleCustomInputChange}
            onKeyDown={handleInputKeyDown}
            disabled={isDisabled}
            placeholder={t('management.schedules.customMinutes.placeholder')}
            aria-label={t('management.schedules.customMinutes.aria')}
            className={`schedule-interval-picker-input${isCustomInputValid ? '' : ' has-error'}`}
          />
          <span className="schedule-interval-picker-suffix">
            {t('management.schedules.customMinutes.suffix')}
          </span>
          <Button
            variant="outline"
            size="sm"
            onClick={handleApply}
            disabled={isDisabled || !isCustomInputValid}
          >
            {t('management.schedules.customMinutes.apply')}
          </Button>
        </div>
      )}
    </div>
  );
});

export default ScheduleIntervalPicker;
