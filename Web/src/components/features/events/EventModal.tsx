import React, { useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { CalendarDays, Trash2, Calendar, Check, BarChart3 } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { useEvents } from '@contexts/EventContext';
import { useTimezone } from '@contexts/TimezoneContext';
import { useTimeFilter } from '@contexts/TimeFilterContext';
import { getEffectiveTimezone, getDateInTimezone } from '@utils/timezone';
import { getEventColorVar } from '@utils/eventColors';
import DateTimePicker from '@components/common/DateTimePicker';
import type { Event, CreateEventRequest, UpdateEventRequest } from '../../../types';

interface EventModalProps {
  event: Event | null; // null for create, Event for edit
  onClose: () => void;
  onSave: () => void;
}

// Color indexes 1-8 for event colors
const COLOR_INDEXES = [1, 2, 3, 4, 5, 6, 7, 8];

const EventModal: React.FC<EventModalProps> = ({ event, onClose, onSave }) => {
  const { t } = useTranslation();
  const { createEvent, updateEvent, deleteEvent } = useEvents();
  const { use24HourFormat, useLocalTimezone } = useTimezone();
  const { setTimeRange, setSelectedEventIds } = useTimeFilter();
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showStartPicker, setShowStartPicker] = useState(false);
  const [showEndPicker, setShowEndPicker] = useState(false);
  const timezone = getEffectiveTimezone(useLocalTimezone);
  const todayParts = getDateInTimezone(new Date(), timezone);
  const todayMinDate = new Date(todayParts.year, todayParts.month, todayParts.day, 0, 0, 0, 0);

  // Form state - using Date objects now
  const [name, setName] = useState(event?.name || '');
  const [description, setDescription] = useState(event?.description || '');
  const [startDateTime, setStartDateTime] = useState<Date>(() => {
    if (event) {
      return new Date(event.startTimeUtc);
    }
    const now = new Date();
    now.setMinutes(0);
    now.setSeconds(0, 0);
    return now;
  });
  const [endDateTime, setEndDateTime] = useState<Date>(() => {
    if (event) {
      return new Date(event.endTimeUtc);
    }
    const later = new Date();
    later.setHours(later.getHours() + 4);
    later.setMinutes(0);
    later.setSeconds(0, 0);
    return later;
  });

  // Use existing event colorIndex, or default to 1
  const [colorIndex, setColorIndex] = useState(event?.colorIndex ?? 1);

  // Format date/time for display
  const formatDateTime = (date: Date): string => {
    const dateStr = date.toLocaleDateString(undefined, {
      timeZone: timezone,
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      year: 'numeric'
    });
    const timeStr = date.toLocaleTimeString(undefined, {
      timeZone: timezone,
      hour: 'numeric',
      minute: '2-digit',
      hour12: !use24HourFormat
    });
    return t('events.modal.dateAt', { date: dateStr, time: timeStr });
  };

  const isBeforeToday = useCallback(
    (date: Date): boolean => {
      const dateParts = getDateInTimezone(date, timezone);
      if (dateParts.year !== todayParts.year) {
        return dateParts.year < todayParts.year;
      }
      if (dateParts.month !== todayParts.month) {
        return dateParts.month < todayParts.month;
      }
      return dateParts.day < todayParts.day;
    },
    [timezone, todayParts]
  );

  const handleSubmit = useCallback(
    async (e: React.FormEvent) => {
      e.preventDefault();
      setError(null);

      if (!name.trim()) {
        setError(t('events.modal.errors.nameRequired'));
        return;
      }

      if (isBeforeToday(startDateTime)) {
        setError(t('events.modal.errors.startInPast'));
        return;
      }

      const startTime = Math.floor(startDateTime.getTime() / 1000);
      const endTime = Math.floor(endDateTime.getTime() / 1000);

      if (endTime <= startTime) {
        setError(t('events.modal.errors.endAfterStart'));
        return;
      }

      setSaving(true);
      try {
        const data: CreateEventRequest | UpdateEventRequest = {
          name: name.trim(),
          description: description.trim() || undefined,
          startTime,
          endTime,
          colorIndex
        };

        if (event) {
          await updateEvent(event.id, data);
        } else {
          await createEvent(data);
        }
        onSave();
      } catch (err) {
        setError(err instanceof Error ? err.message : t('events.modal.errors.saveFailed'));
      } finally {
        setSaving(false);
      }
    },
    [
      name,
      description,
      startDateTime,
      endDateTime,
      colorIndex,
      event,
      createEvent,
      updateEvent,
      onSave,
      t,
      isBeforeToday
    ]
  );

  const handleDeleteClick = useCallback(() => {
    setShowDeleteConfirm(true);
  }, []);

  const handleDeleteConfirm = useCallback(async () => {
    if (!event) return;

    setDeleting(true);
    try {
      await deleteEvent(event.id);
      onSave();
    } catch (err) {
      setError(err instanceof Error ? err.message : t('events.modal.errors.deleteFailed'));
      setShowDeleteConfirm(false);
    } finally {
      setDeleting(false);
    }
  }, [event, deleteEvent, onSave, t]);

  const handleViewOnDashboard = useCallback(() => {
    if (!event) return;

    // Set the event filter to show only downloads tagged to this event
    // Use 'live' time range to show all stats for the event
    setSelectedEventIds([event.id]);
    setTimeRange('live');

    // Close modal and navigate to dashboard via custom event
    onClose();
    window.dispatchEvent(new CustomEvent('navigate-to-tab', { detail: { tab: 'dashboard' } }));
  }, [event, setSelectedEventIds, setTimeRange, onClose]);

  return (
    <>
      <Modal
        opened={true}
        onClose={onClose}
        title={
          <div className="flex items-center gap-2">
            <CalendarDays className="w-5 h-5 text-[var(--theme-primary)]" />
            <span>{event ? t('events.modal.editTitle') : t('events.modal.createTitle')}</span>
          </div>
        }
        size="lg"
      >
        <form onSubmit={handleSubmit} className="space-y-4">
          {/* Error */}
          {error && (
            <div className="p-3 rounded-lg bg-[color-mix(in_srgb,var(--theme-status-error)_10%,transparent)] border border-[color-mix(in_srgb,var(--theme-status-error)_30%,transparent)]">
              <p className="text-sm text-[var(--theme-status-error)]">{error}</p>
            </div>
          )}

          {/* Name */}
          <div>
            <label className="block text-sm font-medium text-[var(--theme-text-primary)] mb-1">
              {t('events.modal.labels.name')} *
            </label>
            <input
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={t('events.modal.placeholders.name')}
              className="w-full px-3 py-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] text-[var(--theme-text-primary)] placeholder-[var(--theme-text-secondary)] focus:outline-none focus:border-[var(--theme-primary)]"
              autoFocus
            />
          </div>

          {/* Description */}
          <div>
            <label className="block text-sm font-medium text-[var(--theme-text-primary)] mb-1">
              {t('events.modal.labels.description')}
            </label>
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder={t('events.modal.placeholders.description')}
              rows={3}
              className="w-full px-3 py-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] text-[var(--theme-text-primary)] placeholder-[var(--theme-text-secondary)] focus:outline-none focus:border-[var(--theme-primary)] resize-none"
            />
          </div>

          {/* Date/Time */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <label className="block text-sm font-medium text-[var(--theme-text-primary)] mb-1">
                {t('events.modal.labels.startDateTime')} *
              </label>
              <button
                type="button"
                onClick={() => setShowStartPicker(true)}
                className="w-full min-w-0 px-3 py-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] text-[var(--theme-text-primary)] text-left hover:border-[var(--theme-primary)] focus:outline-none focus:border-[var(--theme-primary)] transition-colors flex items-center gap-2"
              >
                <Calendar className="w-4 h-4 text-[var(--theme-text-secondary)] flex-shrink-0" />
                <span className="truncate text-sm min-w-0 flex-1">
                  {formatDateTime(startDateTime)}
                </span>
              </button>
            </div>
            <div>
              <label className="block text-sm font-medium text-[var(--theme-text-primary)] mb-1">
                {t('events.modal.labels.endDateTime')} *
              </label>
              <button
                type="button"
                onClick={() => setShowEndPicker(true)}
                className="w-full min-w-0 px-3 py-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] text-[var(--theme-text-primary)] text-left hover:border-[var(--theme-primary)] focus:outline-none focus:border-[var(--theme-primary)] transition-colors flex items-center gap-2"
              >
                <Calendar className="w-4 h-4 text-[var(--theme-text-secondary)] flex-shrink-0" />
                <span className="truncate text-sm min-w-0 flex-1">
                  {formatDateTime(endDateTime)}
                </span>
              </button>
            </div>
          </div>

          {/* Color */}
          <div>
            <label className="block text-sm font-medium text-[var(--theme-text-primary)] mb-2">
              {t('events.modal.labels.color')}
            </label>
            <div className="flex flex-wrap gap-2">
              {COLOR_INDEXES.map((idx) => {
                const isSelected = colorIndex === idx;
                return (
                  <button
                    key={idx}
                    type="button"
                    onClick={() => setColorIndex(idx)}
                    className={`w-8 h-8 rounded-lg transition-all flex items-center justify-center ${
                      isSelected ? 'scale-110' : 'hover:scale-105'
                    }`}
                    style={{
                      backgroundColor: getEventColorVar(idx),
                      boxShadow: isSelected
                        ? `0 0 0 2px var(--theme-bg-secondary), 0 0 0 4px var(--theme-primary)`
                        : 'none'
                    }}
                  >
                    {isSelected && (
                      <Check className="w-5 h-5 text-white drop-shadow-[0_1px_1px_rgba(0,0,0,0.5)]" />
                    )}
                  </button>
                );
              })}
            </div>
          </div>

          {/* Actions */}
          <div className="flex flex-col gap-3 pt-4 border-t border-[var(--theme-border-primary)] sm:flex-row sm:items-center sm:justify-between">
            <div className="flex flex-wrap gap-2">
              {event && (
                <>
                  <Button
                    type="button"
                    color="red"
                    variant="outline"
                    onClick={handleDeleteClick}
                    leftSection={<Trash2 className="w-4 h-4" />}
                  >
                    {t('events.modal.actions.delete')}
                  </Button>
                  <Button
                    type="button"
                    variant="outline"
                    onClick={handleViewOnDashboard}
                    leftSection={<BarChart3 className="w-4 h-4" />}
                  >
                    {t('events.modal.actions.viewStats')}
                  </Button>
                </>
              )}
            </div>
            <div className="flex flex-wrap gap-2 sm:justify-end">
              <Button
                type="button"
                variant="outline"
                onClick={onClose}
                disabled={saving || deleting}
              >
                {t('actions.cancel')}
              </Button>
              <Button type="submit" variant="filled" color="blue" loading={saving}>
                {event ? t('events.modal.actions.saveChanges') : t('events.modal.actions.create')}
              </Button>
            </div>
          </div>
        </form>

        {/* Start DateTime Picker */}
        {showStartPicker && (
          <DateTimePicker
            value={startDateTime}
            onChange={(date) => {
              setStartDateTime(date);
              // If end time is before new start time, auto-adjust it
              if (date >= endDateTime) {
                const newEnd = new Date(date);
                newEnd.setHours(newEnd.getHours() + 4);
                setEndDateTime(newEnd);
              }
            }}
            onClose={() => setShowStartPicker(false)}
            title={t('events.modal.selectStartDateTime')}
            minDate={todayMinDate}
          />
        )}

        {/* End DateTime Picker */}
        {showEndPicker && (
          <DateTimePicker
            value={endDateTime}
            onChange={setEndDateTime}
            onClose={() => setShowEndPicker(false)}
            title={t('events.modal.selectEndDateTime')}
            minDate={startDateTime > todayMinDate ? startDateTime : todayMinDate}
          />
        )}
      </Modal>

      {/* Delete Confirmation Modal - rendered as sibling, not nested */}
      <Modal
        opened={showDeleteConfirm}
        onClose={() => setShowDeleteConfirm(false)}
        title={
          <div className="flex items-center gap-2">
            <Trash2 className="w-5 h-5 text-[var(--theme-status-error)]" />
            <span>{t('events.modal.deleteTitle')}</span>
          </div>
        }
        size="sm"
      >
        <div className="space-y-4">
          <p className="text-[var(--theme-text-secondary)]">
            {t('events.modal.deleteConfirm', { name: event?.name })}
          </p>
          <p className="text-sm text-[var(--theme-text-muted)]">
            {t('events.modal.deleteWarning')}
          </p>
          <div className="flex justify-end gap-2 pt-4 border-t border-[var(--theme-border-primary)]">
            <Button
              variant="outline"
              onClick={() => setShowDeleteConfirm(false)}
              disabled={deleting}
            >
              {t('actions.cancel')}
            </Button>
            <Button variant="filled" color="red" onClick={handleDeleteConfirm} loading={deleting}>
              {t('events.modal.actions.delete')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default EventModal;
