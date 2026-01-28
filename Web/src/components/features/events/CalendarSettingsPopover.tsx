import React, { useState, useRef, useEffect, useLayoutEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { createPortal } from 'react-dom';
import { Settings, RotateCcw, Eye, Calendar, Hash, EyeOff, Layers, LayoutGrid, CalendarRange } from 'lucide-react';
import { useCalendarSettings, type WeekStartDay, type EventOpacity, type EventDisplayStyle } from '@contexts/CalendarSettingsContext';

interface CalendarSettingsPopoverProps {
  position?: 'left' | 'right';
}

// Toggle button for selecting between options
const ToggleButton: React.FC<{
  options: { value: string; label: string }[];
  value: string;
  onChange: (value: string) => void;
}> = ({ options, value, onChange }) => (
  <div className="flex rounded-md p-0.5 bg-[var(--theme-bg-tertiary)]">
    {options.map(option => {
      const isActive = value === option.value;
      return (
        <button
          key={option.value}
          type="button"
          onClick={(e) => {
            e.preventDefault();
            e.stopPropagation();
            onChange(option.value);
          }}
          className="px-2.5 py-1 text-xs font-medium rounded cursor-pointer"
          style={{
            backgroundColor: isActive ? 'var(--theme-primary)' : 'transparent',
            color: isActive ? 'var(--theme-button-text)' : 'var(--theme-text-secondary)'
          }}
        >
          {option.label}
        </button>
      );
    })}
  </div>
);

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

const VIEWPORT_PADDING = 12;
const POPOVER_WIDTH = 280;
const POPOVER_MAX_HEIGHT = 520;

interface PopoverPosition {
  x: number;
  y: number;
  openUpward: boolean;
}

const CalendarSettingsPopover: React.FC<CalendarSettingsPopoverProps> = ({
  position = 'right'
}) => {
  const { t } = useTranslation();
  const { settings, updateSettings, resetSettings } = useCalendarSettings();
  const [isOpen, setIsOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const [popoverPos, setPopoverPos] = useState<PopoverPosition | null>(null);

  // Reset position when closing
  useEffect(() => {
    if (!isOpen) {
      setPopoverPos(null);
    }
  }, [isOpen]);

  // Close on click outside
  useEffect(() => {
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

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
    }
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen]);

  // Close on scroll (except scrolling inside popover)
  useEffect(() => {
    if (!isOpen) return;

    const handleScroll = (e: Event) => {
      if (popoverRef.current?.contains(e.target as Node)) return;
      setIsOpen(false);
    };

    window.addEventListener('scroll', handleScroll, { passive: true, capture: true });
    return () => window.removeEventListener('scroll', handleScroll, { capture: true });
  }, [isOpen]);

  // Close on escape key
  useEffect(() => {
    if (!isOpen) return;

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };

    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [isOpen]);

  // Calculate position synchronously before paint (same pattern as EnhancedDropdown)
  useLayoutEffect(() => {
    if (!isOpen || !triggerRef.current) return;

    const calculatePosition = (): PopoverPosition | null => {
      if (!triggerRef.current) return null;

      const triggerRect = triggerRef.current.getBoundingClientRect();
      const viewportWidth = document.documentElement.clientWidth || window.innerWidth;
      const viewportHeight = window.innerHeight;
      
      // Use actual popover dimensions if rendered, otherwise use defaults
      const popoverWidth = popoverRef.current?.offsetWidth || POPOVER_WIDTH;
      const popoverHeight = popoverRef.current?.offsetHeight || POPOVER_MAX_HEIGHT;

      // Calculate horizontal position - align right edge with trigger right edge
      let x = position === 'left'
        ? triggerRect.left
        : triggerRect.right - popoverWidth;

      // Clamp X within viewport
      const maxX = viewportWidth - popoverWidth - VIEWPORT_PADDING;
      x = Math.min(Math.max(x, VIEWPORT_PADDING), Math.max(VIEWPORT_PADDING, maxX));

      // Calculate vertical position - prefer below trigger
      const spaceBelow = viewportHeight - triggerRect.bottom - VIEWPORT_PADDING;
      const spaceAbove = triggerRect.top - VIEWPORT_PADDING;
      const openUpward = spaceBelow < popoverHeight && spaceAbove > spaceBelow;

      let y = openUpward
        ? triggerRect.top - popoverHeight - 8
        : triggerRect.bottom + 8;

      // Clamp Y within viewport
      y = Math.max(VIEWPORT_PADDING, Math.min(y, viewportHeight - popoverHeight - VIEWPORT_PADDING));

      return { x, y, openUpward };
    };

    const pos = calculatePosition();
    if (pos) {
      setPopoverPos(pos);
    }
  }, [isOpen, position]);

  // Re-adjust position after popover renders (to use actual measured dimensions)
  useLayoutEffect(() => {
    if (!isOpen || !popoverPos || !triggerRef.current || !popoverRef.current) return;

    const triggerRect = triggerRef.current.getBoundingClientRect();
    const popoverRect = popoverRef.current.getBoundingClientRect();
    const viewportWidth = document.documentElement.clientWidth || window.innerWidth;
    const viewportHeight = window.innerHeight;

    // Recalculate X with actual width
    let newX = position === 'left'
      ? triggerRect.left
      : triggerRect.right - popoverRect.width;
    
    const maxX = viewportWidth - popoverRect.width - VIEWPORT_PADDING;
    newX = Math.min(Math.max(newX, VIEWPORT_PADDING), Math.max(VIEWPORT_PADDING, maxX));

    // Recalculate Y with actual height
    const spaceBelow = viewportHeight - triggerRect.bottom - VIEWPORT_PADDING;
    const spaceAbove = triggerRect.top - VIEWPORT_PADDING;
    const openUpward = spaceBelow < popoverRect.height && spaceAbove > spaceBelow;

    let newY = openUpward
      ? triggerRect.top - popoverRect.height - 8
      : triggerRect.bottom + 8;

    newY = Math.max(VIEWPORT_PADDING, Math.min(newY, viewportHeight - popoverRect.height - VIEWPORT_PADDING));

    // Only update if position changed significantly
    if (Math.abs(newX - popoverPos.x) > 0.5 || Math.abs(newY - popoverPos.y) > 0.5) {
      setPopoverPos({ x: newX, y: newY, openUpward });
    }
  }, [isOpen, popoverPos, position]);

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        onClick={() => setIsOpen(!isOpen)}
        className="p-2 rounded-md border"
        title={t('events.calendar.settings.title')}
        style={{
          backgroundColor: isOpen ? 'var(--theme-primary-subtle)' : 'transparent',
          borderColor: isOpen ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
          color: isOpen ? 'var(--theme-primary)' : 'var(--theme-text-secondary)'
        }}
      >
        <Settings className="w-4 h-4" />
      </button>

      {isOpen && popoverPos && createPortal(
        <div
          ref={popoverRef}
          className="fixed rounded-xl border shadow-2xl overflow-hidden z-[70] flex flex-col"
          style={{
            left: popoverPos.x,
            top: popoverPos.y,
            width: 'min(280px, calc(100vw - 24px))',
            maxHeight: 'min(520px, calc(100vh - 24px))',
            backgroundColor: 'var(--theme-card-bg)',
            borderColor: 'var(--theme-card-border)',
            boxShadow: '0 20px 50px -12px rgba(0, 0, 0, 0.5)',
            animation: `${popoverPos.openUpward ? 'dropdownSlideUp' : 'dropdownSlideDown'} 0.15s cubic-bezier(0.16, 1, 0.3, 1)`
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
            <button
              type="button"
              onClick={(e) => {
                e.preventDefault();
                e.stopPropagation();
                resetSettings();
              }}
              className="flex items-center gap-1.5 px-2 py-1 rounded-md text-xs font-medium cursor-pointer hover:bg-[var(--theme-bg-hover)] hover:text-[var(--theme-text-primary)] text-[var(--theme-text-muted)]"
              title={t('events.calendar.settings.resetToDefaults')}
            >
              <RotateCcw className="w-3 h-3" />
              {t('events.calendar.settings.reset')}
            </button>
          </div>

          {/* Settings */}
          <div className="px-4 py-2 space-y-1 overflow-y-auto flex-1 min-h-0">
            {/* Event Opacity */}
            <div className="flex items-start justify-between gap-3 py-2.5">
              <div className="flex items-start gap-2.5 min-w-0">
                <div className="w-7 h-7 rounded-md flex items-center justify-center flex-shrink-0 bg-[var(--theme-bg-tertiary)]">
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
                <ToggleButton
                  options={[
                    { value: 'transparent', label: t('events.calendar.settings.eventStyle.soft') },
                    { value: 'solid', label: t('events.calendar.settings.eventStyle.solid') }
                  ]}
                  value={settings.eventOpacity}
                  onChange={(v) => updateSettings({ eventOpacity: v as EventOpacity })}
                />
              </div>
            </div>

            <div className="border-t border-[var(--theme-border-secondary)] my-1" />

            {/* Event Layout */}
            <div className="flex items-start justify-between gap-3 py-2.5">
              <div className="flex items-start gap-2.5 min-w-0">
                <div className="w-7 h-7 rounded-md flex items-center justify-center flex-shrink-0 bg-[var(--theme-bg-tertiary)]">
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
                <ToggleButton
                  options={[
                    { value: 'spanning', label: t('events.calendar.settings.eventLayout.bars') },
                    { value: 'daily', label: t('events.calendar.settings.eventLayout.daily') }
                  ]}
                  value={settings.eventDisplayStyle}
                  onChange={(v) => updateSettings({ eventDisplayStyle: v as EventDisplayStyle })}
                />
              </div>
            </div>

            <div className="border-t border-[var(--theme-border-secondary)] my-1" />

            {/* Week Start Day */}
            <div className="flex items-start justify-between gap-3 py-2.5">
              <div className="flex items-start gap-2.5 min-w-0">
                <div className="w-7 h-7 rounded-md flex items-center justify-center flex-shrink-0 bg-[var(--theme-bg-tertiary)]">
                  <Calendar className="w-4 h-4 text-[var(--theme-icon-cyan)]" />
                </div>
                <div className="min-w-0">
                  <div className="text-sm font-medium text-[var(--theme-text-primary)]">
                    {t('events.calendar.settings.weekStart.title')}
                  </div>
                </div>
              </div>
              <div className="flex-shrink-0">
                <ToggleButton
                  options={[
                    { value: 'sunday', label: t('events.calendar.settings.weekStart.sunday') },
                    { value: 'monday', label: t('events.calendar.settings.weekStart.monday') }
                  ]}
                  value={settings.weekStartDay}
                  onChange={(v) => updateSettings({ weekStartDay: v as WeekStartDay })}
                />
              </div>
            </div>

            <div className="border-t border-[var(--theme-border-secondary)] my-1" />

            {/* Show Week Numbers */}
            <div className="flex items-start justify-between gap-3 py-2.5">
              <div className="flex items-start gap-2.5 min-w-0">
                <div className="w-7 h-7 rounded-md flex items-center justify-center flex-shrink-0 bg-[var(--theme-bg-tertiary)]">
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

            <div className="border-t border-[var(--theme-border-secondary)] my-1" />

            {/* Show Adjacent Months */}
            <div className="flex items-start justify-between gap-3 py-2.5">
              <div className="flex items-start gap-2.5 min-w-0">
                <div className="w-7 h-7 rounded-md flex items-center justify-center flex-shrink-0 bg-[var(--theme-bg-tertiary)]">
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

            <div className="border-t border-[var(--theme-border-secondary)] my-1" />

            {/* Hide Ended Events */}
            <div className="flex items-start justify-between gap-3 py-2.5">
              <div className="flex items-start gap-2.5 min-w-0">
                <div className="w-7 h-7 rounded-md flex items-center justify-center flex-shrink-0 bg-[var(--theme-bg-tertiary)]">
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

            <div className="border-t border-[var(--theme-border-secondary)] my-1" />

            {/* Compact Mode */}
            <div className="flex items-start justify-between gap-3 py-2.5">
              <div className="flex items-start gap-2.5 min-w-0">
                <div className="w-7 h-7 rounded-md flex items-center justify-center flex-shrink-0 bg-[var(--theme-bg-tertiary)]">
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
