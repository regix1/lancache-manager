import { noAutofill } from '@utils/autofill';
import React, { useCallback, useEffect, useMemo, useRef, useState, memo } from 'react';
import { useTranslation } from 'react-i18next';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Button } from '@components/ui/Button';
import { useMediaQuery } from '@hooks/useMediaQuery';
import { getScheduleIntervalOptions } from './constants';
import CustomScheduleModal from './custom-schedule/CustomScheduleModal';
import type { CustomSchedule } from './custom-schedule/types';
import './ScheduleIntervalPicker.css';

const CUSTOM_SENTINEL = 'custom' as const;
const SCHEDULE_SENTINEL = 'schedule' as const;
const MIN_CUSTOM_MINUTES = 1;
const MAX_CUSTOM_MINUTES = 59;

interface ScheduleIntervalPickerProps {
  intervalHours: number;
  isDisabled: boolean;
  /** Picking a plain interval means "no custom schedule". A caller that also passes
      `onCustomScheduleChange` must clear the saved schedule in the SAME save that writes the
      interval - otherwise the schedule keeps winning on the server and the interval the user
      just chose never takes effect. */
  onChange: (hours: number) => void;
  /** The saved schedule, or null when the service runs on its plain interval. */
  customSchedule?: CustomSchedule | null;
  /** Omitted on a surface that cannot persist a schedule, which also hides the entry that
      opens the builder. */
  onCustomScheduleChange?: (schedule: CustomSchedule) => void;
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
  customSchedule = null,
  onCustomScheduleChange,
  variant = 'field'
}: ScheduleIntervalPickerProps) {
  const { t } = useTranslation();
  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  // The ghost treatment only exists in the desktop table (its CSS is scoped to the
  // same breakpoint); on folded mobile tiles the picker is a plain full-width field.
  const isTableLayout = useMediaQuery('(min-width: 768px)');

  // A sub-hour interval displays as a regular "Every N minutes" option in the closed
  // trigger; the minutes editor only exists while this popover is open. The editor
  // used to live in the flow below the trigger, which permanently grew any row whose
  // schedule held a custom interval - the popover keeps every row one height.
  const [customOpen, setCustomOpen] = useState(false);
  const [customMinutes, setCustomMinutes] = useState('30');
  const [scheduleOpen, setScheduleOpen] = useState(false);
  // Latches on the first open and never falls back, so the builder is mounted from then on and
  // closing it plays the modal's exit rather than snapping it out of the tree. Read during
  // render rather than held in state: opening already re-renders this component.
  const scheduleEverOpened = useRef(false);
  if (scheduleOpen) scheduleEverOpened.current = true;

  const dropdownOptions = useMemo((): DropdownOption[] => {
    const standardOptions = getScheduleIntervalOptions(t);
    // The builder entry appears where the surface can persist a schedule, and also wherever one
    // is already saved - a saved schedule is what the trigger reads, so without the entry the
    // trigger would render blank. Once saved, that same entry carries a state label rather than
    // the action label, and picking it reopens the builder to edit what is there.
    const options: DropdownOption[] =
      onCustomScheduleChange || customSchedule
        ? [
            ...standardOptions,
            {
              value: SCHEDULE_SENTINEL,
              label: customSchedule
                ? t('management.schedules.customSchedule.savedLabel')
                : t('management.schedules.customSchedule.optionLabel')
            }
          ]
        : standardOptions;
    const currentVal =
      intervalHours === 0 ? '0' : intervalHours === -1 ? '-1' : String(intervalHours);
    if (intervalHours > 0 && !standardOptions.some((opt) => opt.value === currentVal)) {
      return [{ value: currentVal, label: formatIntervalLabel(intervalHours, t) }, ...options];
    }
    return options;
  }, [intervalHours, customSchedule, onCustomScheduleChange, t]);

  // A saved schedule wins over the interval on the server, so the trigger has to read as the
  // schedule even though the interval value is still stored beside it untouched.
  const savedValue = customSchedule
    ? SCHEDULE_SENTINEL
    : intervalHours === 0
      ? '0'
      : intervalHours === -1
        ? '-1'
        : String(intervalHours);
  // While either editor is open the trigger reads as that entry; dismissing without applying
  // falls straight back to the saved label, because this value is derived and never stored.
  const dropdownValue = customOpen
    ? CUSTOM_SENTINEL
    : scheduleOpen
      ? SCHEDULE_SENTINEL
      : savedValue;

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
      if (value === SCHEDULE_SENTINEL) {
        // Commits nothing, and stays shut where there is no handler to save what it would build.
        if (!onCustomScheduleChange) return;
        setCustomOpen(false);
        setScheduleOpen(true);
        return;
      }
      const parsed = Number(value);
      if (Number.isFinite(parsed)) {
        setCustomOpen(false);
        onChange(parsed);
      }
    },
    [intervalHours, onChange, onCustomScheduleChange]
  );

  const handleScheduleApply = useCallback(
    (schedule: CustomSchedule) => {
      setScheduleOpen(false);
      onCustomScheduleChange?.(schedule);
    },
    [onCustomScheduleChange]
  );

  const handleScheduleClose = useCallback(() => {
    setScheduleOpen(false);
  }, []);

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
        /* The menu defaults to the trigger's width, and the desktop ghost trigger is
           content-sized - a short value like "Daily" would leave the menu too narrow
           for its longest options, truncating them. On mobile the trigger is a
           full-width field again, so the menu should follow it. */
        dropdownWidth={variant === 'ghost' && isTableLayout ? '12rem' : undefined}
      />
      {customOpen && (
        <div
          className="schedule-interval-popover themed-border-radius-sm"
          role="group"
          aria-label={t('management.schedules.customMinutes.aria')}
          onKeyDown={handlePopoverKeyDown}
        >
          <input
            {...noAutofill}
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
            color="secondary"
            size="sm"
            onClick={handleApply}
            disabled={isDisabled || customMinutesValue === null}
          >
            {t('management.schedules.customMinutes.apply')}
          </Button>
        </div>
      )}
      {/* The builder is a Modal, which portals to the document body - a picker sitting in a table
          row or inside another modal would otherwise have its panel clipped by the scroll area.
          It is mounted only once someone asks for it: its body reads the browser's whole zone
          database and walks the schedule, and one picker renders per service row, so a page of
          rows nobody opened would pay all of it. The mount is kept afterwards so the modal can
          play its own closing animation. */}
      {onCustomScheduleChange && scheduleEverOpened.current && (
        <CustomScheduleModal
          opened={scheduleOpen}
          schedule={customSchedule}
          isDisabled={isDisabled}
          onClose={handleScheduleClose}
          onApply={handleScheduleApply}
        />
      )}
    </div>
  );
});

export default ScheduleIntervalPicker;
