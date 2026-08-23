import React, { useState, useRef, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { createPortal } from 'react-dom';
import {
  Settings,
  RotateCcw,
  Eye,
  Calendar,
  Hash,
  EyeOff,
  Layers,
  LayoutGrid,
  CalendarRange
} from 'lucide-react';
import { useCalendarSettings } from '@contexts/useCalendarSettings';
import { Tooltip } from '@components/ui/Tooltip';
import { Button } from '@components/ui/Button';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { useAnchoredPanel } from '@hooks/useAnchoredPanel';
import { POPOVER_GUTTER_PX } from '@utils/viewportClamp';
import type {
  WeekStartDay,
  EventOpacity,
  EventDisplayStyle
} from '@contexts/CalendarSettingsContext.types';

interface CalendarSettingsPopoverProps {
  position?: 'left' | 'right';
}

// Toggle switch for boolean options
const CheckboxToggle: React.FC<{
  checked: boolean;
  onChange: (checked: boolean) => void;
}> = ({ checked, onChange }) => (
  <button
    type="button"
    onClick={(e) => {
      e.preventDefault();
      e.stopPropagation();
      onChange(!checked);
    }}
    className="w-9 h-5 rounded-full relative cursor-pointer"
    style={{
      backgroundColor: checked ? 'var(--theme-primary)' : 'var(--theme-bg-tertiary)',
      border: checked ? 'none' : '1px solid var(--theme-border-secondary)'
    }}
  >
    <span
      className="absolute top-0.5 w-4 h-4 rounded-full shadow-sm"
      style={{
        left: checked ? '18px' : '2px',
        backgroundColor: checked ? 'var(--theme-button-text)' : 'var(--theme-text-muted)'
      }}
    />
  </button>
);

/** Gap between the trigger and the popover, whichever side it opens on. */
const TRIGGER_GAP = 8;

const CalendarSettingsPopover: React.FC<CalendarSettingsPopoverProps> = ({
  position = 'right'
}) => {
  const { t } = useTranslation();
  const { settings, updateSettings, resetSettings } = useCalendarSettings();
  const [isOpen, setIsOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);

  const closePopover = useCallback((): void => setIsOpen(false), []);

  const {
    present,
    closing,
    position: popoverPos
  } = useAnchoredPanel({
    open: isOpen,
    anchorRef: triggerRef,
    panelRef: popoverRef,
    onClose: closePopover,
    gutter: POPOVER_GUTTER_PX,
    align: position === 'left' ? 'left' : 'right',
    gap: TRIGGER_GAP
  });

  // Click-outside only. Escape belongs to the shared hook, and the close-on-scroll
  // listener is gone: the popover is positioned on the page and carried by its
  // trigger, so a scroll no longer mispositions it.
  useEffect(() => {
    if (!isOpen) return;

    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;
      if (
        popoverRef.current &&
        !popoverRef.current.contains(target) &&
        triggerRef.current &&
        !triggerRef.current.contains(target)
      ) {
        setIsOpen(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen]);

  return (
    <>
      <Tooltip content={t('events.calendar.settings.title')} position="top">
        <Button
          ref={triggerRef}
          type="button"
          variant="menu"
          size="sm"
          open={isOpen}
          onClick={() => setIsOpen(!isOpen)}
          className="btn-icon-square btn-icon-square--sm"
          aria-label={t('events.calendar.settings.title')}
        >
          <Settings className="w-4 h-4" />
        </Button>
      </Tooltip>

      {/* Rendered while `present` (not just `isOpen`) so the exit animation plays.
          While closing, the entrance class comes off so the exit keyframe is the only
          animation on the element. */}
      {present &&
        createPortal(
          <div
            ref={popoverRef}
            className={`absolute rounded-lg border overflow-hidden z-[90] flex flex-col calendar-settings-popover motion-reduce:animate-none ${
              closing
                ? popoverPos.openUpward
                  ? 'animate-[dropdownSlideOutUp_0.14s_ease-in_forwards]'
                  : 'animate-[dropdownSlideOutDown_0.14s_ease-in_forwards]'
                : popoverPos.openUpward
                  ? 'calendar-settings-popover--upward'
                  : 'calendar-settings-popover--downward'
            }`}
            style={{
              left: popoverPos.left,
              top: popoverPos.top,
              pointerEvents: closing ? 'none' : undefined
            }}
          >
            {/* Header */}
            <div className="px-4 py-3 flex items-center justify-between bg-[var(--theme-bg-tertiary)] border-b border-[var(--theme-border-secondary)]">
              <div className="flex items-center gap-2">
                <Settings className="w-4 h-4 text-[var(--theme-primary)]" />
                <span className="text-sm font-semibold text-[var(--theme-text-primary)]">
                  {t('events.calendar.settings.title')}
                </span>
              </div>
              <Button
                type="button"
                variant="transparent"
                size="xs"
                onClick={(e) => {
                  e.preventDefault();
                  e.stopPropagation();
                  resetSettings();
                }}
                className="flex items-center gap-1.5 px-2 py-1 rounded-md text-xs font-medium cursor-pointer hover:bg-[var(--theme-bg-hover)] hover:text-[var(--theme-text-primary)] text-[var(--theme-text-muted)]"
                leftSection={<RotateCcw className="w-3 h-3" />}
              >
                {t('events.calendar.settings.reset')}
              </Button>
            </div>

            {/* Settings — hairlines from .divided-list; row py-2.5 keeps gap without space-y */}
            <div className="px-4 py-2 divided-list overflow-y-auto flex-1 min-h-0">
              {/* Event Opacity */}
              <div className="flex items-start justify-between gap-3 py-2.5">
                <div className="flex items-start gap-2.5 min-w-0">
                  <div className="icon-box w-7 h-7 bg-[var(--theme-bg-tertiary)]">
                    <Layers className="w-4 h-4 text-[var(--theme-icon-purple)]" />
                  </div>
                  <div className="min-w-0">
                    <div className="text-sm font-medium text-[var(--theme-text-primary)]">
                      {t('events.calendar.settings.eventStyle.title')}
                    </div>
                    <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                      {t('events.calendar.settings.eventStyle.description')}
                    </div>
                  </div>
                </div>
                <div className="flex-shrink-0">
                  <SegmentedControl
                    size="sm"
                    options={[
                      {
                        value: 'transparent',
                        label: t('events.calendar.settings.eventStyle.soft')
                      },
                      { value: 'solid', label: t('events.calendar.settings.eventStyle.solid') }
                    ]}
                    value={settings.eventOpacity}
                    onChange={(v) => updateSettings({ eventOpacity: v as EventOpacity })}
                  />
                </div>
              </div>

              {/* Event Layout */}
              <div className="flex items-start justify-between gap-3 py-2.5">
                <div className="flex items-start gap-2.5 min-w-0">
                  <div className="icon-box w-7 h-7 bg-[var(--theme-bg-tertiary)]">
                    <LayoutGrid className="w-4 h-4 text-[var(--theme-icon-blue)]" />
                  </div>
                  <div className="min-w-0">
                    <div className="text-sm font-medium text-[var(--theme-text-primary)]">
                      {t('events.calendar.settings.eventLayout.title')}
                    </div>
                    <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                      {t('events.calendar.settings.eventLayout.description')}
                    </div>
                  </div>
                </div>
                <div className="flex-shrink-0">
                  <SegmentedControl
                    size="sm"
                    options={[
                      { value: 'spanning', label: t('events.calendar.settings.eventLayout.bars') },
                      { value: 'daily', label: t('events.calendar.settings.eventLayout.daily') }
                    ]}
                    value={settings.eventDisplayStyle}
                    onChange={(v) => updateSettings({ eventDisplayStyle: v as EventDisplayStyle })}
                  />
                </div>
              </div>

              {/* Week Start Day */}
              <div className="flex items-start justify-between gap-3 py-2.5">
                <div className="flex items-start gap-2.5 min-w-0">
                  <div className="icon-box w-7 h-7 bg-[var(--theme-bg-tertiary)]">
                    <Calendar className="w-4 h-4 text-[var(--theme-icon-cyan)]" />
                  </div>
                  <div className="min-w-0">
                    <div className="text-sm font-medium text-[var(--theme-text-primary)]">
                      {t('events.calendar.settings.weekStart.title')}
                    </div>
                  </div>
                </div>
                <div className="flex-shrink-0">
                  <SegmentedControl
                    size="sm"
                    options={[
                      { value: 'sunday', label: t('events.calendar.settings.weekStart.sunday') },
                      { value: 'monday', label: t('events.calendar.settings.weekStart.monday') }
                    ]}
                    value={settings.weekStartDay}
                    onChange={(v) => updateSettings({ weekStartDay: v as WeekStartDay })}
                  />
                </div>
              </div>

              {/* Show Week Numbers */}
              <div className="flex items-start justify-between gap-3 py-2.5">
                <div className="flex items-start gap-2.5 min-w-0">
                  <div className="icon-box w-7 h-7 bg-[var(--theme-bg-tertiary)]">
                    <Hash className="w-4 h-4 text-[var(--theme-icon-orange)]" />
                  </div>
                  <div className="min-w-0">
                    <div className="text-sm font-medium text-[var(--theme-text-primary)]">
                      {t('events.calendar.settings.weekNumbers.title')}
                    </div>
                    <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                      {t('events.calendar.settings.weekNumbers.description')}
                    </div>
                  </div>
                </div>
                <div className="flex-shrink-0">
                  <CheckboxToggle
                    checked={settings.showWeekNumbers}
                    onChange={(v) => updateSettings({ showWeekNumbers: v })}
                  />
                </div>
              </div>

              {/* Show Adjacent Months */}
              <div className="flex items-start justify-between gap-3 py-2.5">
                <div className="flex items-start gap-2.5 min-w-0">
                  <div className="icon-box w-7 h-7 bg-[var(--theme-bg-tertiary)]">
                    <CalendarRange className="w-4 h-4 text-[var(--theme-icon-purple)]" />
                  </div>
                  <div className="min-w-0">
                    <div className="text-sm font-medium text-[var(--theme-text-primary)]">
                      {t('events.calendar.settings.adjacentMonths.title')}
                    </div>
                    <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                      {t('events.calendar.settings.adjacentMonths.description')}
                    </div>
                  </div>
                </div>
                <div className="flex-shrink-0">
                  <CheckboxToggle
                    checked={settings.showAdjacentMonths}
                    onChange={(v) => updateSettings({ showAdjacentMonths: v })}
                  />
                </div>
              </div>

              {/* Hide Ended Events */}
              <div className="flex items-start justify-between gap-3 py-2.5">
                <div className="flex items-start gap-2.5 min-w-0">
                  <div className="icon-box w-7 h-7 bg-[var(--theme-bg-tertiary)]">
                    <EyeOff className="w-4 h-4 text-[var(--theme-icon-red)]" />
                  </div>
                  <div className="min-w-0">
                    <div className="text-sm font-medium text-[var(--theme-text-primary)]">
                      {t('events.calendar.settings.hideEnded.title')}
                    </div>
                    <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                      {t('events.calendar.settings.hideEnded.description')}
                    </div>
                  </div>
                </div>
                <div className="flex-shrink-0">
                  <CheckboxToggle
                    checked={settings.hideEndedEvents}
                    onChange={(v) => updateSettings({ hideEndedEvents: v })}
                  />
                </div>
              </div>

              {/* Compact Mode */}
              <div className="flex items-start justify-between gap-3 py-2.5">
                <div className="flex items-start gap-2.5 min-w-0">
                  <div className="icon-box w-7 h-7 bg-[var(--theme-bg-tertiary)]">
                    <Eye className="w-4 h-4 text-[var(--theme-icon-green)]" />
                  </div>
                  <div className="min-w-0">
                    <div className="text-sm font-medium text-[var(--theme-text-primary)]">
                      {t('events.calendar.settings.compactView.title')}
                    </div>
                    <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                      {t('events.calendar.settings.compactView.description')}
                    </div>
                  </div>
                </div>
                <div className="flex-shrink-0">
                  <CheckboxToggle
                    checked={settings.compactMode}
                    onChange={(v) => updateSettings({ compactMode: v })}
                  />
                </div>
              </div>
            </div>

            {/* Footer hint */}
            <div className="px-4 py-2.5 text-[11px] bg-[var(--theme-bg-secondary)] text-[var(--theme-text-muted)] border-t border-[var(--theme-border-secondary)]">
              {t('events.calendar.settings.autoSave')}
            </div>
          </div>,
          document.body
        )}
    </>
  );
};

export default CalendarSettingsPopover;
