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
  /** 'field' keeps the bordered input look for forms and modals. 'ghost' renders the
      closed trigger as plain text for table rows; the field chrome returns on hover,
      keyboard focus and while the menu is open. */
  variant?: 'field' | 'ghost';
}

function formatIntervalLabel(hours: number, t: ReturnType<typeof useTranslation>['t']): string {
  if (hours <= 0) return '';
  if (hours < 1) {
    const count = Math.round(hours * 60);
    return t('management.schedules.everyNMinutes', { count });
  }
  return t('management.schedules.everyNHours', { count: hours });
}

function parseCustomMinutes(raw: string): number | null {
  const trimmed = raw.trim();
  if (trimmed.length === 0) return null;
  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed) || !Number.isInteger(parsed)) return null;
  if (parsed < MIN_CUSTOM_MINUTES || parsed > MAX_CUSTOM_MINUTES) return null;
  return parsed;
}

const ScheduleIntervalPicker = memo(function ScheduleIntervalPicker({
  intervalHours,
  isDisabled,
  onChange,
  variant = 'field'
}: ScheduleIntervalPickerProps) {
  const { t } = useTranslation();
  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // A sub-hour interval displays as a regular "Every N minutes" option in the closed
  // trigger; the minutes editor only exists while this popover is open. The editor
  // used to live in the flow below the trigger, which permanently grew any row whose
  // schedule held a custom interval - the popover keeps every row one height.
  const [customOpen, setCustomOpen] = useState(false);
  const [customMinutes, setCustomMinutes] = useState('30');

  const dropdownOptions = useMemo((): DropdownOption[] => {
    const standardOptions = getScheduleIntervalOptions(t);
    const currentVal =
      intervalHours === 0 ? '0' : intervalHours === -1 ? '-1' : String(intervalHours);
    if (intervalHours > 0 && !standardOptions.some((opt) => opt.value === currentVal)) {
      return [
        { value: currentVal, label: formatIntervalLabel(intervalHours, t) },
        ...standardOptions
      ];
    }
    return standardOptions;
  }, [intervalHours, t]);

  // While the minutes popover is open the trigger reads as Custom; dismissing the
  // popover without applying falls straight back to the saved interval's label.
  const savedValue =
    intervalHours === 0 ? '0' : intervalHours === -1 ? '-1' : String(intervalHours);
  const dropdownValue = customOpen ? CUSTOM_SENTINEL : savedValue;

  const customMinutesValue = parseCustomMinutes(customMinutes);

  const handleDropdownChange = useCallback(
    (value: string) => {
      if (value === CUSTOM_SENTINEL) {
        setCustomMinutes(
          intervalHours > 0 && intervalHours < 1 ? String(Math.round(intervalHours * 60)) : '30'
        );
        setCustomOpen(true);
        return;
      }
      const parsed = Number(value);
      if (Number.isFinite(parsed)) {
        setCustomOpen(false);
        onChange(parsed);
      }
    },
    [intervalHours, onChange]
  );

  // Focus lands in the input as soon as the popover mounts so Enter applies directly.
  useEffect(() => {
    if (!customOpen) return;
    const id = requestAnimationFrame(() => {
      inputRef.current?.focus();
      inputRef.current?.select();
    });
    return () => cancelAnimationFrame(id);
  }, [customOpen]);

  // Click-away dismissal, the same contract as the dropdown panel itself.
  useEffect(() => {
    if (!customOpen) return;
    const handlePointerDown = (event: MouseEvent) => {
      if (
        containerRef.current &&
        event.target instanceof Node &&
        !containerRef.current.contains(event.target)
      ) {
        setCustomOpen(false);
      }
    };
    document.addEventListener('mousedown', handlePointerDown);
    return () => document.removeEventListener('mousedown', handlePointerDown);
  }, [customOpen]);

  const handleApply = useCallback(() => {
    const minutes = parseCustomMinutes(customMinutes);
    if (minutes === null) return;
    setCustomOpen(false);
    onChange(minutes / 60);
  }, [customMinutes, onChange]);

  const handleInputKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLInputElement>) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        handleApply();
      }
    },
    [handleApply]
  );

  const handlePopoverKeyDown = useCallback((event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape') {
      event.stopPropagation();
      setCustomOpen(false);
    }
  }, []);

  const handleCustomInputChange = useCallback((event: React.ChangeEvent<HTMLInputElement>) => {
    setCustomMinutes(event.target.value);
  }, []);

  return (
    <div
      ref={containerRef}
      className={`schedule-interval-picker${
        variant === 'ghost' ? ' schedule-interval-picker--ghost' : ''
      }`}
    >
      <EnhancedDropdown
        options={dropdownOptions}
        value={dropdownValue}
        onChange={handleDropdownChange}
        disabled={isDisabled}
        variant="button"
      />
      {customOpen && (
        <div
          className="schedule-interval-popover themed-border-radius-sm"
          role="group"
          aria-label={t('management.schedules.customMinutes.aria')}
          onKeyDown={handlePopoverKeyDown}
        >
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
            className={`schedule-interval-picker-input focus-ring${
              customMinutesValue === null ? ' has-error' : ''
            }`}
          />
          <span className="schedule-interval-picker-suffix">
            {t('management.schedules.customMinutes.suffix')}
          </span>
          <Button
            variant="filled"
            color="green"
            size="sm"
            onClick={handleApply}
            disabled={isDisabled || customMinutesValue === null}
          >
            {t('management.schedules.customMinutes.apply')}
          </Button>
        </div>
      )}
    </div>
  );
});

export default ScheduleIntervalPicker;
