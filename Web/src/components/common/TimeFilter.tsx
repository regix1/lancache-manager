import React, { useState, useMemo, useRef, useLayoutEffect, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { createPortal } from 'react-dom';
import { Clock, Calendar, Radio, Info, ChevronDown, Check, ChevronLeft, ChevronRight, Filter } from 'lucide-react';
import { useTimeFilter, type TimeRange } from '@contexts/TimeFilterContext';
import { useEvents } from '@contexts/EventContext';
import DateRangePicker from './DateRangePicker';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { getEventColorVar } from '@utils/eventColors';

interface TimeFilterProps {
  disabled?: boolean;
  iconOnly?: boolean;
}

const TimeFilter: React.FC<TimeFilterProps> = ({ disabled = false, iconOnly = false }) => {
  const { t } = useTranslation();
  const {
    timeRange,
    setTimeRange,
    customStartDate,
    customEndDate,
    setCustomStartDate,
    setCustomEndDate,
    selectedEventIds,
    toggleEventId,
    clearEventFilter
  } = useTimeFilter();

  const { events } = useEvents();

  const [isOpen, setIsOpen] = useState(false);
  const [showDatePicker, setShowDatePicker] = useState(false);
  const [dropdownPosition, setDropdownPosition] = useState({ top: 0, left: 0 });
  const [dropdownStyle, setDropdownStyle] = useState<{ animation: string }>({ animation: '' });
  const [isMobile, setIsMobile] = useState(false);
  const [eventPage, setEventPage] = useState(0);
  const touchStartX = useRef<number | null>(null);

  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  // Sort events: active first, then upcoming, then past
  const sortedEvents = useMemo(() => {
    const now = new Date();
    return [...events].sort((a, b) => {
      const aStart = new Date(a.startTimeUtc);
      const aEnd = new Date(a.endTimeUtc);
      const bStart = new Date(b.startTimeUtc);
      const bEnd = new Date(b.endTimeUtc);

      const aIsActive = now >= aStart && now <= aEnd;
      const bIsActive = now >= bStart && now <= bEnd;
      const aIsUpcoming = now < aStart;
      const bIsUpcoming = now < bStart;

      if (aIsActive && !bIsActive) return -1;
      if (!aIsActive && bIsActive) return 1;
      if (aIsUpcoming && !bIsUpcoming) return -1;
      if (!aIsUpcoming && bIsUpcoming) return 1;

      return aStart.getTime() - bStart.getTime();
    });
  }, [events]);

  const getEventStatus = (startUtc: string, endUtc: string) => {
    const now = new Date();
    const start = new Date(startUtc);
    const end = new Date(endUtc);
    if (now >= start && now <= end) return 'active';
    if (now < start) return 'upcoming';
    return 'past';
  };

  const formatEventDateRange = (startUtc: string, endUtc: string) => {
    const start = new Date(startUtc);
    const end = new Date(endUtc);
    const startStr = start.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
    const endStr = end.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
    return startStr === endStr ? startStr : `${startStr} - ${endStr}`;
  };

  // Time range options - matching old structure
  const timeOptions = useMemo(() => [
    {
      value: 'live',
      label: t('common.timeFilter.options.live.label'),
      shortLabel: t('common.timeFilter.options.live.shortLabel'),
      description: t('common.timeFilter.options.live.description'),
      icon: Radio,
      rightLabel: t('common.timeFilter.options.live.rightLabel')
    },
    {
      value: '1h',
      label: t('common.timeFilter.options.1h.label'),
      shortLabel: t('common.timeFilter.options.1h.shortLabel'),
      description: t('common.timeFilter.options.1h.description'),
      icon: Clock,
      rightLabel: t('common.timeFilter.options.1h.rightLabel')
    },
    {
      value: '6h',
      label: t('common.timeFilter.options.6h.label'),
      shortLabel: t('common.timeFilter.options.6h.shortLabel'),
      description: t('common.timeFilter.options.6h.description'),
      icon: Clock,
      rightLabel: t('common.timeFilter.options.6h.rightLabel')
    },
    {
      value: '12h',
      label: t('common.timeFilter.options.12h.label'),
      shortLabel: t('common.timeFilter.options.12h.shortLabel'),
      description: t('common.timeFilter.options.12h.description'),
      icon: Clock,
      rightLabel: t('common.timeFilter.options.12h.rightLabel')
    },
    {
      value: '24h',
      label: t('common.timeFilter.options.24h.label'),
      shortLabel: t('common.timeFilter.options.24h.shortLabel'),
      description: t('common.timeFilter.options.24h.description'),
      icon: Clock,
      rightLabel: t('common.timeFilter.options.24h.rightLabel')
    },
    {
      value: '7d',
      label: t('common.timeFilter.options.7d.label'),
      shortLabel: t('common.timeFilter.options.7d.shortLabel'),
      description: t('common.timeFilter.options.7d.description'),
      icon: Calendar,
      rightLabel: t('common.timeFilter.options.7d.rightLabel')
    },
    {
      value: '30d',
      label: t('common.timeFilter.options.30d.label'),
      shortLabel: t('common.timeFilter.options.30d.shortLabel'),
      description: t('common.timeFilter.options.30d.description'),
      icon: Calendar,
      rightLabel: t('common.timeFilter.options.30d.rightLabel')
    },
    {
      value: 'custom',
      label: t('common.timeFilter.options.custom.label'),
      shortLabel: t('common.timeFilter.options.custom.shortLabel'),
      description: t('common.timeFilter.options.custom.description'),
      icon: Calendar,
      rightLabel: t('common.timeFilter.options.custom.rightLabel')
    }
  ], [t]);

  // Calculate position before paint - for portal rendering
  useLayoutEffect(() => {
    if (!isOpen || !buttonRef.current) return;

    const calculatePosition = () => {
      if (!buttonRef.current) return null;

      const rect = buttonRef.current.getBoundingClientRect();
      const dropdownHeight = 400; // Approximate max height
      const dropdownWidth = 256; // w-64 = 16rem = 256px
      const spaceBelow = window.innerHeight - rect.bottom;
      const spaceAbove = rect.top;
      const shouldOpenUpward = spaceBelow < dropdownHeight && spaceAbove > dropdownHeight;

      // Calculate horizontal position - align right edge with button right edge
      let left = rect.right - dropdownWidth;

      // Ensure dropdown doesn't go off-screen left
      if (left < 8) {
        left = 8;
      }

      // Ensure dropdown doesn't go off-screen right
      if (left + dropdownWidth > window.innerWidth - 8) {
        left = window.innerWidth - dropdownWidth - 8;
      }

      // Calculate vertical position
      const top = shouldOpenUpward
        ? rect.top - dropdownHeight - 8
        : rect.bottom + 4;

      return { top, left, shouldOpenUpward };
    };

    const pos = calculatePosition();
    if (pos) {
      setDropdownPosition({ top: pos.top, left: pos.left });
      setDropdownStyle({
        animation: `${pos.shouldOpenUpward ? 'dropdownSlideUp' : 'dropdownSlideDown'} 0.15s cubic-bezier(0.16, 1, 0.3, 1)`
      });
    }
  }, [isOpen]);

  useEffect(() => {
    const updateIsMobile = () => {
      setIsMobile(window.innerWidth < 640);
    };
    updateIsMobile();
    window.addEventListener('resize', updateIsMobile);
    return () => window.removeEventListener('resize', updateIsMobile);
  }, []);

  useEffect(() => {
    setEventPage(0);
  }, [sortedEvents.length, isMobile]);

  // Event listeners
  useEffect(() => {
    if (!isOpen) return;

    const handleClickOutside = (e: MouseEvent) => {
      const target = e.target as Node;
      if (!dropdownRef.current?.contains(target) && !buttonRef.current?.contains(target)) {
        setIsOpen(false);
      }
    };

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };

    // Close on scroll to prevent dropdown from being mispositioned
    const handleScroll = (e: Event) => {
      // Ignore scroll events originating from inside the dropdown
      if (dropdownRef.current && dropdownRef.current.contains(e.target as Node)) {
        return;
      }
      setIsOpen(false);
    };

    document.addEventListener('mousedown', handleClickOutside);
    document.addEventListener('keydown', handleEscape);
    window.addEventListener('scroll', handleScroll, true);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
      document.removeEventListener('keydown', handleEscape);
      window.removeEventListener('scroll', handleScroll, true);
    };
  }, [isOpen]);

  const handleTimeRangeChange = useCallback((value: string) => {
    const timeValue = value as TimeRange;
    setTimeRange(timeValue);
    if (timeValue === 'custom') {
      setShowDatePicker(true);
    } else {
      setShowDatePicker(false);
    }
    setIsOpen(false);
  }, [setTimeRange]);

  const eventsPerPage = isMobile ? 2 : 5;
  const totalEventPages = Math.max(1, Math.ceil(sortedEvents.length / eventsPerPage));
  const pagedEvents = sortedEvents.slice(
    eventPage * eventsPerPage,
    eventPage * eventsPerPage + eventsPerPage
  );

  const handleEventToggle = useCallback((e: React.MouseEvent, eventId: number) => {
    e.stopPropagation();
    toggleEventId(eventId);
  }, [toggleEventId]);

  const goToPrevPage = useCallback(() => {
    setEventPage((prev) => (prev - 1 + totalEventPages) % totalEventPages);
  }, [totalEventPages]);

  const goToNextPage = useCallback(() => {
    setEventPage((prev) => (prev + 1) % totalEventPages);
  }, [totalEventPages]);

  const handleTouchStart = (e: React.TouchEvent) => {
    touchStartX.current = e.touches[0]?.clientX ?? null;
  };

  const handleTouchEnd = (e: React.TouchEvent) => {
    if (touchStartX.current === null) return;
    const endX = e.changedTouches[0]?.clientX ?? null;
    if (endX === null) return;
    const deltaX = endX - touchStartX.current;
    touchStartX.current = null;
    if (Math.abs(deltaX) < 40) return;
    if (deltaX > 0) {
      goToPrevPage();
    } else {
      goToNextPage();
    }
  };

  // Generate custom label for date ranges
  const getTimeRangeTriggerLabel = () => {
    if (timeRange === 'custom' && customStartDate && customEndDate) {
      const start = customStartDate.toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric'
      });
      const end = customEndDate.toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric'
      });
      return `${start} - ${end}`;
    }
    const option = timeOptions.find(o => o.value === timeRange);
    return option?.shortLabel || t('common.timeFilter.options.live.shortLabel');
  };

  const selectedTimeOption = timeOptions.find(o => o.value === timeRange);

  return (
    <>
      <div className="flex items-center gap-2">
        {/* Main Trigger Button - matching EnhancedDropdown style */}
        <div className="relative">
          <button
            ref={buttonRef}
            type="button"
            onClick={() => !disabled && setIsOpen(!isOpen)}
            disabled={disabled}
            aria-label={t('common.timeFilter.title')}
            className={`ed-trigger w-full px-3 py-2 sm:px-3 sm:py-2 rounded-lg border text-left flex items-center justify-between text-sm bg-[var(--theme-card-bg)] text-[var(--theme-text-primary)] ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'} ${isOpen ? 'border-[var(--theme-border-focus)]' : 'border-[var(--theme-border-primary)]'}`}
          >
            <div className={`flex items-center flex-1 truncate ${iconOnly ? 'justify-center' : 'gap-1.5'}`}>
              {iconOnly ? (
                <Filter className="flex-shrink-0 text-[var(--theme-primary)]" size={16} />
              ) : (
                <>
                  {selectedTimeOption?.icon && (
                    <selectedTimeOption.icon className="flex-shrink-0 text-[var(--theme-primary)]" size={16} />
                  )}
                  <span className="font-medium">{getTimeRangeTriggerLabel()}</span>
                </>
              )}
            </div>
            {!iconOnly && (
              <ChevronDown
                size={16}
                className={`flex-shrink-0 transition-transform duration-200 text-[var(--theme-text-primary)] ${isOpen ? 'rotate-180' : ''}`}
              />
            )}
          </button>

          {/* Dropdown - rendered via portal to escape stacking context */}
          {isOpen && createPortal(
            <div
              ref={dropdownRef}
              className="ed-dropdown fixed w-64 rounded-lg border overflow-hidden bg-[var(--theme-bg-secondary)] border-[var(--theme-border-primary)] max-w-[calc(100vw-32px)] shadow-[0_10px_25px_-5px_rgba(0,0,0,0.3),0_8px_10px_-6px_rgba(0,0,0,0.2)] z-[85]"
              style={{
                top: dropdownPosition.top,
                left: dropdownPosition.left,
                animation: dropdownStyle.animation
              }}
            >
              {/* Title */}
              <div
                className="px-3 py-2 text-sm font-medium border-b text-[var(--theme-text-secondary)] border-[var(--theme-border-primary)] bg-[var(--theme-bg-secondary)]"
              >
                {t('common.timeFilter.title')}
              </div>

              <CustomScrollbar maxHeight="min(70vh, 32rem)" paddingMode="compact">
                {/* Time Range Options */}
                <div className="py-1">
                  {timeOptions.map((option) => {
                    const isSelected = option.value === timeRange;
                    return (
                      <button
                        key={option.value}
                        type="button"
                        onClick={() => handleTimeRangeChange(option.value)}
                        className={`ed-option w-full px-3 py-2.5 text-left text-sm cursor-pointer ${isSelected ? 'ed-option-selected' : ''}`}
                        title={option.description}
                      >
                        <div className="flex items-start gap-3">
                          <div className="flex flex-col flex-1 min-w-0">
                            <span className={`font-medium truncate ${isSelected ? 'text-[var(--theme-primary)]' : 'text-[var(--theme-text-primary)]'}`}>
                              {option.label}
                            </span>
                            {option.description && (
                              <span className="text-xs mt-0.5 leading-relaxed text-[var(--theme-text-secondary)]">
                                {option.description}
                              </span>
                            )}
                          </div>
                          {option.rightLabel && (
                            <span className={`flex-shrink-0 text-xs font-medium ${isSelected ? 'text-[var(--theme-primary)]' : 'text-[var(--theme-text-secondary)]'}`}>
                              {option.rightLabel}
                            </span>
                          )}
                        </div>
                      </button>
                    );
                  })}
                </div>

                {/* Events Section - Only show if there are events */}
                {sortedEvents.length > 0 && (
                  <>
                    <div
                      className="px-3 py-2 text-xs font-medium border-t mt-1 mb-1 flex items-center justify-between text-[var(--theme-text-muted)] border-[var(--theme-border-primary)] bg-[var(--theme-bg-tertiary)]"
                    >
                      <div className="flex items-center gap-2">
                        <span>{t('common.timeFilter.filterByEvents')}</span>
                        {sortedEvents.length > eventsPerPage && (
                          <div className="flex items-center gap-1.5">
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                goToPrevPage();
                              }}
                              className="h-6 w-6 rounded flex items-center justify-center text-[var(--theme-text-secondary)] hover:text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-secondary)]"
                              aria-label={t('common.timeFilter.previousEventsPage')}
                            >
                              <ChevronLeft size={14} />
                            </button>
                            <span className="text-[10px] text-[var(--theme-text-secondary)]">
                              {eventPage + 1}/{totalEventPages}
                            </span>
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                goToNextPage();
                              }}
                              className="h-6 w-6 rounded flex items-center justify-center text-[var(--theme-text-secondary)] hover:text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-secondary)]"
                              aria-label={t('common.timeFilter.nextEventsPage')}
                            >
                              <ChevronRight size={14} />
                            </button>
                          </div>
                        )}
                      </div>
                      {selectedEventIds.length > 0 && (
                        <button
                          onClick={(e) => {
                            e.stopPropagation();
                            clearEventFilter();
                          }}
                          className="text-xs px-1.5 py-0.5 rounded hover:opacity-80 transition-opacity text-[var(--theme-primary)] bg-[var(--theme-primary-muted)]"
                        >
                          {t('common.clear')}
                        </button>
                      )}
                    </div>
                    <div className="py-1" onTouchStart={handleTouchStart} onTouchEnd={handleTouchEnd}>
                      {pagedEvents.map((event) => {
                        const isSelected = selectedEventIds.includes(event.id);
                        const status = getEventStatus(event.startTimeUtc, event.endTimeUtc);
                        const colorVar = event.colorIndex ? getEventColorVar(event.colorIndex) : 'var(--theme-primary)';

                        return (
                          <button
                            key={event.id}
                            type="button"
                            onClick={(e) => handleEventToggle(e, event.id)}
                            className={`ed-option w-full px-3 py-2.5 text-left text-sm cursor-pointer ${isSelected ? 'ed-option-selected' : ''}`}
                          >
                            <div className="flex items-center gap-3">
                              {/* Checkbox */}
                              <div
                                className="flex-shrink-0 w-4 h-4 rounded flex items-center justify-center"
                                style={{
                                  backgroundColor: isSelected ? colorVar : 'transparent',
                                  border: isSelected ? 'none' : '2px solid var(--theme-border-primary)'
                                }}
                              >
                                {isSelected && <Check size={12} className="text-white" strokeWidth={3} />}
                              </div>

                              {/* Color dot */}
                              <div
                                className="w-2 h-2 rounded-full flex-shrink-0"
                                style={{ backgroundColor: colorVar }}
                              />

                              {/* Content */}
                              <div className="flex flex-col flex-1 min-w-0">
                                <div className="flex items-center gap-1.5">
                                  <span className={`font-medium truncate ${isSelected ? 'text-[var(--theme-primary)]' : 'text-[var(--theme-text-primary)]'}`}>
                                    {event.name}
                                  </span>
                                  {status === 'active' && (
                                    <span
                                      className="px-1.5 py-0.5 text-[10px] rounded-full font-medium"
                                      style={{
                                        backgroundColor: 'color-mix(in srgb, var(--theme-success) 20%, transparent)',
                                        color: 'var(--theme-success)'
                                      }}
                                    >
                                      {t('common.timeFilter.liveBadge')}
                                    </span>
                                  )}
                                </div>
                                <span className="text-xs mt-0.5 text-[var(--theme-text-secondary)]">
                                  {formatEventDateRange(event.startTimeUtc, event.endTimeUtc)}
                                </span>
                              </div>
                            </div>
                          </button>
                        );
                      })}
                    </div>
                  </>
                )}
              </CustomScrollbar>

              {/* Footer Note */}
              <div
                className="px-3 py-2.5 text-xs border-t flex items-start gap-2 text-[var(--theme-text-secondary)] border-[var(--theme-border-primary)] bg-[var(--theme-bg-tertiary)]"
              >
                <Info className="flex-shrink-0 mt-0.5 text-[var(--theme-warning)]" size={14} />
                <span className="leading-relaxed">
                  {sortedEvents.length > 0
                    ? t('common.timeFilter.footer.combinedFilters')
                    : t('common.timeFilter.footer.historicalNote')}
                </span>
              </div>
            </div>,
            document.body
          )}
        </div>

      </div>

      {showDatePicker && (
        <DateRangePicker
          startDate={customStartDate}
          endDate={customEndDate}
          onStartDateChange={setCustomStartDate}
          onEndDateChange={setCustomEndDate}
          onClose={() => {
            setShowDatePicker(false);
            if (!customStartDate || !customEndDate) {
              setTimeRange('live');
            }
          }}
        />
      )}
    </>
  );
};

export default TimeFilter;
