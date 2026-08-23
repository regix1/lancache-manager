import React, { useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { CalendarDays, Trash2, Calendar, Check } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Button } from '@components/ui/Button';
import FormField from '@components/ui/FormField';
import { useEvents } from '@contexts/useEvents';
import { useReaderClock } from '@hooks/useReaderClock';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { formatTimestamp, type TimestampSettings } from '@utils/dateTimeFormat';
import { getEventColorVar } from '@utils/eventColors';
import DateTimePicker from '@components/common/DateTimePicker';
import { getErrorMessage } from '@utils/error';
import type { Event, CreateEventRequest, UpdateEventRequest } from '../../../types';
import { APP_EVENTS } from '@utils/constants';

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
  const clock = useReaderClock();
  const { setTimeRange, setSelectedEventIds } = useTimeFilter();
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showStartPicker, setShowStartPicker] = useState(false);
  const [showEndPicker, setShowEndPicker] = useState(false);
  // The floor the two DateTimePickers below are given. They compare it against cells they build
  // in the browser's own calendar, so it is midnight of the browser's today. Taking the day from
  // a display timezone instead lands the floor on the neighbouring day, which either lets an
  // event be created that has already started or makes today unselectable.
  const todayMinDate = new Date();
  todayMinDate.setHours(0, 0, 0, 0);

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
    const settings: TimestampSettings = { ...clock, forceYear: false };
    const dateStr = formatTimestamp(date, { ...settings, style: 'dateOnly' });
    const timeStr = formatTimestamp(date, { ...settings, style: 'timeOnly' });
    return t('events.modal.dateAt', { date: dateStr, time: timeStr });
  };

  // Same floor the pickers enforce, so the form cannot reject a date the calendar offered.
  // Read at call time rather than closed over, which keeps this callback stable and keeps a
  // modal left open across midnight from checking against yesterday.
  const isBeforeToday = useCallback((date: Date): boolean => {
    const startOfToday = new Date();
    startOfToday.setHours(0, 0, 0, 0);
    return date < startOfToday;
  }, []);

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
        setError(getErrorMessage(err) || t('events.modal.errors.saveFailed'));
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
      setError(getErrorMessage(err) || t('events.modal.errors.deleteFailed'));
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
    window.dispatchEvent(
      new CustomEvent(APP_EVENTS.NAVIGATE_TO_TAB, { detail: { tab: 'dashboard' } })
    );
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
            {event && (
              <Button
                type="button"
                variant="transparent"
                size="sm"
                onClick={handleViewOnDashboard}
                className="text-sm font-normal text-[var(--theme-primary)] hover:underline"
              >
                {t('events.modal.actions.viewStats')}
              </Button>
            )}
          </div>
        }
        size="lg"
      >
        <form onSubmit={handleSubmit} className="space-y-4">
          {/* Error */}
          {error && (
            <div className="p-3 rounded-lg bg-[var(--theme-error-faint)] border border-[var(--theme-error-strong)]">
              <p className="text-sm text-[var(--theme-status-error)]">{error}</p>
            </div>
          )}

          {/* Name */}
          <div>
            <FormField label={t('events.modal.labels.name')} required>
              {(field) => (
                <input
                  {...field}
                  type="text"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  placeholder={t('events.modal.placeholders.name')}
                  className="themed-input w-full px-3 py-2"
                  autoFocus
                />
              )}
            </FormField>
          </div>

          {/* Description */}
          <div>
            <FormField label={t('events.modal.labels.description')}>
              {(field) => (
                <textarea
                  {...field}
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  placeholder={t('events.modal.placeholders.description')}
                  rows={3}
                  className="themed-input w-full px-3 py-2 resize-none"
                />
              )}
            </FormField>
          </div>

          {/* Date/Time */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <FormField label={t('events.modal.labels.startDateTime')} required>
                {(field) => (
                  <Button
                    {...field}
                    type="button"
                    variant="transparent"
                    onClick={() => setShowStartPicker(true)}
                    className="w-full min-w-0 px-3 py-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] text-[var(--theme-text-primary)] text-left hover:border-[var(--theme-primary)] focus:outline-none focus:border-[var(--theme-primary)] transition-colors justify-start gap-2 font-normal"
                  >
                    <Calendar className="w-4 h-4 text-[var(--theme-text-secondary)] flex-shrink-0" />
                    <span className="truncate text-sm min-w-0 flex-1">
                      {formatDateTime(startDateTime)}
                    </span>
                  </Button>
                )}
              </FormField>
            </div>
            <div>
              <FormField label={t('events.modal.labels.endDateTime')} required>
                {(field) => (
                  <Button
                    {...field}
                    type="button"
                    variant="transparent"
                    onClick={() => setShowEndPicker(true)}
                    className="w-full min-w-0 px-3 py-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] text-[var(--theme-text-primary)] text-left hover:border-[var(--theme-primary)] focus:outline-none focus:border-[var(--theme-primary)] transition-colors justify-start gap-2 font-normal"
                  >
                    <Calendar className="w-4 h-4 text-[var(--theme-text-secondary)] flex-shrink-0" />
                    <span className="truncate text-sm min-w-0 flex-1">
                      {formatDateTime(endDateTime)}
                    </span>
                  </Button>
                )}
              </FormField>
            </div>
          </div>

          {/* Color */}
          <div>
            <label className="form-field-label">{t('events.modal.labels.color')}</label>
            <div className="flex flex-wrap gap-2">
              {COLOR_INDEXES.map((idx) => {
                const isSelected = colorIndex === idx;
                return (
                  <button
                    key={idx}
                    type="button"
                    onClick={() => setColorIndex(idx)}
                    aria-pressed={isSelected}
                    className={`event-color-swatch w-7 h-7 themed-border-radius-sm transition flex items-center justify-center ${
                      isSelected ? 'scale-110' : 'hover:scale-105'
                    }`}
                    style={{ '--event-swatch-color': getEventColorVar(idx) } as React.CSSProperties}
                  >
                    {isSelected && (
                      <Check className="w-4 h-4 text-white drop-shadow-[0_1px_1px_rgba(0,0,0,0.5)]" />
                    )}
                  </button>
                );
              })}
            </div>
          </div>

          {/* Actions */}
          <div className="event-modal-actions flex flex-row items-center justify-between gap-3 pt-4 border-t border-[var(--theme-border-primary)]">
            {event ? (
              <Button
                type="button"
                color="destructive"
                variant="filled"
                onClick={handleDeleteClick}
              >
                {t('events.modal.actions.delete')}
              </Button>
            ) : (
              <div />
            )}
            <div className="flex items-center gap-2">
              <Button
                type="button"
                variant="default"
                className="event-modal-cancel"
                onClick={onClose}
                disabled={saving || deleting}
              >
                {t('actions.cancel')}
              </Button>
              <Button type="submit" variant="filled" color="primary" loading={saving}>
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
      <ConfirmationModal
        opened={showDeleteConfirm}
        onClose={() => setShowDeleteConfirm(false)}
        onConfirm={handleDeleteConfirm}
        title={t('events.modal.deleteTitle')}
        confirmLabel={t('events.modal.actions.delete')}
        confirmColor="red"
        loading={deleting}
        icon={<Trash2 className="w-6 h-6 text-[var(--theme-status-error)]" />}
      >
        <p className="text-[var(--theme-text-secondary)]">
          {t('events.modal.deleteConfirm', { name: event?.name })}
        </p>
        <p className="text-sm text-[var(--theme-text-muted)]">{t('events.modal.deleteWarning')}</p>
      </ConfirmationModal>
    </>
  );
};

export default EventModal;
