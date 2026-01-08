import React, { useState, useRef, useEffect, useLayoutEffect } from 'react';
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

const CalendarSettingsPopover: React.FC<CalendarSettingsPopoverProps> = ({
  position = 'right'
}) => {
  const { settings, updateSettings, resetSettings } = useCalendarSettings();
  const [isOpen, setIsOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const [popoverPos, setPopoverPos] = useState<{ x: number; y: number } | null>(null);

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

  // Close on scroll
  useEffect(() => {
    if (!isOpen) return;

    const handleScroll = (e: Event) => {
      if (popoverRef.current?.contains(e.target as Node)) return;
      setIsOpen(false);
    };

    window.addEventListener('scroll', handleScroll, { passive: true, capture: true });
    return () => window.removeEventListener('scroll', handleScroll, { capture: true });
  }, [isOpen]);

  // Calculate position
  useLayoutEffect(() => {
    if (!isOpen || !triggerRef.current || !popoverRef.current) return;

    const timer = setTimeout(() => {
      if (!triggerRef.current || !popoverRef.current) return;

      const triggerRect = triggerRef.current.getBoundingClientRect();
      const popoverRect = popoverRef.current.getBoundingClientRect();
      const viewportPadding = 12;
      const popoverWidth = 280;

      let x = position === 'left'
        ? triggerRect.left
        : triggerRect.right - popoverWidth;
      let y = triggerRect.bottom + 8;

      // Clamp X
      if (x + popoverWidth > window.innerWidth - viewportPadding) {
        x = window.innerWidth - popoverWidth - viewportPadding;
      }
      if (x < viewportPadding) {
        x = viewportPadding;
      }

      // If would go off bottom, show above
      const popoverHeight = popoverRect.height || 300;
      if (y + popoverHeight > window.innerHeight - viewportPadding) {
        y = triggerRect.top - popoverHeight - 8;
      }

      y = Math.max(viewportPadding, y);
      setPopoverPos({ x, y });
    }, 10);

    return () => clearTimeout(timer);
  }, [isOpen, position]);

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        onClick={() => setIsOpen(!isOpen)}
        className="p-2 rounded-md border"
        title="Calendar Settings"
        style={{
          backgroundColor: isOpen ? 'var(--theme-primary-subtle)' : 'transparent',
          borderColor: isOpen ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
          color: isOpen ? 'var(--theme-primary)' : 'var(--theme-text-secondary)'
        }}
      >
        <Settings className="w-4 h-4" />
      </button>

      {isOpen && createPortal(
        <div
          ref={popoverRef}
          className="fixed rounded-xl border shadow-2xl overflow-hidden z-[100000]"
          style={{
            left: popoverPos?.x ?? -9999,
            top: popoverPos?.y ?? -9999,
            width: 280,
            visibility: popoverPos ? 'visible' : 'hidden',
            backgroundColor: 'var(--theme-card-bg)',
            borderColor: 'var(--theme-card-border)',
            boxShadow: '0 20px 50px -12px rgba(0, 0, 0, 0.5)'
          }}
        >
          {/* Header */}
          <div
            className="px-4 py-3 flex items-center justify-between"
            style={{
              backgroundColor: 'var(--theme-bg-tertiary)',
              borderBottom: '1px solid var(--theme-border-secondary)'
            }}
          >
            <div className="flex items-center gap-2">
              <Settings className="w-4 h-4 text-[var(--theme-primary)]" />
              <span className="text-sm font-semibold text-[var(--theme-text-primary)]">
                Calendar Settings
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
              title="Reset to defaults"
            >
              <RotateCcw className="w-3 h-3" />
              Reset
            </button>
          </div>

          {/* Settings */}
          <div className="px-4 py-2 space-y-1">
            {/* Event Opacity */}
            <div className="flex items-start justify-between gap-3 py-2.5">
              <div className="flex items-start gap-2.5 min-w-0">
                <div className="w-7 h-7 rounded-md flex items-center justify-center flex-shrink-0 bg-[var(--theme-bg-tertiary)]">
                  <Layers className="w-4 h-4 text-[var(--theme-icon-purple)]" />
                </div>
                <div className="min-w-0">
                  <div className="text-sm font-medium text-[var(--theme-text-primary)]">
                    Event Style
                  </div>
                  <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                    How events appear on the calendar
                  </div>
                </div>
              </div>
              <div className="flex-shrink-0">
                <ToggleButton
                  options={[
                    { value: 'transparent', label: 'Soft' },
                    { value: 'solid', label: 'Solid' }
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
                    Event Layout
                  </div>
                  <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                    Spanning bars or per-day
                  </div>
                </div>
              </div>
              <div className="flex-shrink-0">
                <ToggleButton
                  options={[
                    { value: 'spanning', label: 'Bars' },
                    { value: 'daily', label: 'Daily' }
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
                    Week Starts On
                  </div>
                </div>
              </div>
              <div className="flex-shrink-0">
                <ToggleButton
                  options={[
                    { value: 'sunday', label: 'Sun' },
                    { value: 'monday', label: 'Mon' }
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
                    Show Week Numbers
                  </div>
                  <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                    Display week numbers on the left
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
                    Adjacent Months
                  </div>
                  <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                    Show days from prev/next months
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
                    Hide Ended Events
                  </div>
                  <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                    Don't show events that have ended
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
                    Compact View
                  </div>
                  <div className="text-xs mt-0.5 text-[var(--theme-text-muted)]">
                    Use smaller day cells
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
            Settings are saved automatically
          </div>
        </div>,
        document.body
      )}
    </>
  );
};

export default CalendarSettingsPopover;
