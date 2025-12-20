import React, { useState, useCallback, useMemo, useEffect } from 'react';
import { CalendarDays, Trash2, Calendar, Check } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { useEvents } from '@contexts/EventContext';
import { useTimezone } from '@contexts/TimezoneContext';
import { getEffectiveTimezone } from '@utils/timezone';
import DateTimePicker from '@components/common/DateTimePicker';
import type { Event, CreateEventRequest, UpdateEventRequest } from '../../../types';

interface EventModalProps {
  event: Event | null; // null for create, Event for edit
  onClose: () => void;
  onSave: () => void;
}

// Get event colors from theme CSS variables
const getEventColors = (): string[] => {
  const root = document.documentElement;
  const style = getComputedStyle(root);
  return [
    style.getPropertyValue('--theme-event-1').trim(),
    style.getPropertyValue('--theme-event-2').trim(),
    style.getPropertyValue('--theme-event-3').trim(),
    style.getPropertyValue('--theme-event-4').trim(),
    style.getPropertyValue('--theme-event-5').trim(),
    style.getPropertyValue('--theme-event-6').trim(),
    style.getPropertyValue('--theme-event-7').trim(),
    style.getPropertyValue('--theme-event-8').trim(),
  ].filter(c => c); // Filter out empty values
};

const EventModal: React.FC<EventModalProps> = ({ event, onClose, onSave }) => {
  const { createEvent, updateEvent, deleteEvent } = useEvents();
  const { use24HourFormat, useLocalTimezone } = useTimezone();
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [eventColors, setEventColors] = useState<string[]>([]);
  const [showStartPicker, setShowStartPicker] = useState(false);
  const [showEndPicker, setShowEndPicker] = useState(false);

  // Load theme colors on mount and when theme changes
  useEffect(() => {
    const loadColors = () => setEventColors(getEventColors());
    loadColors();

    // Listen for theme changes
    window.addEventListener('themechange', loadColors);
    return () => window.removeEventListener('themechange', loadColors);
  }, []);

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

  // Format date/time for display
  const formatDateTime = (date: Date): string => {
    const timezone = getEffectiveTimezone(useLocalTimezone);
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
    return `${dateStr} at ${timeStr}`;
  };

  // Use existing event color, or default to first theme color
  const defaultColor = useMemo(() => {
    if (event?.color) return event.color;
    return eventColors[0] || '';
  }, [event?.color, eventColors]);

  const [color, setColor] = useState(defaultColor);

  // Update color if default changes (e.g., theme colors loaded)
  useEffect(() => {
    if (!color && defaultColor) {
      setColor(defaultColor);
    }
  }, [color, defaultColor]);

  const handleSubmit = useCallback(async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!name.trim()) {
      setError('Event name is required');
      return;
    }

    const startTime = Math.floor(startDateTime.getTime() / 1000);
    const endTime = Math.floor(endDateTime.getTime() / 1000);

    if (endTime <= startTime) {
      setError('End time must be after start time');
      return;
    }

    setSaving(true);
    try {
      // Use selected color, or default to first theme color
      const finalColor = color || eventColors[0] || '';
      const data: CreateEventRequest | UpdateEventRequest = {
        name: name.trim(),
        description: description.trim() || undefined,
        startTime,
        endTime,
        color: finalColor
      };

      if (event) {
        await updateEvent(event.id, data);
      } else {
        await createEvent(data);
      }
      onSave();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save event');
    } finally {
      setSaving(false);
    }
  }, [name, description, startDateTime, endDateTime, color, event, createEvent, updateEvent, onSave]);

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
      setError(err instanceof Error ? err.message : 'Failed to delete event');
      setShowDeleteConfirm(false);
    } finally {
      setDeleting(false);
    }
  }, [event, deleteEvent, onSave]);

  return (
    <>
      <Modal
        opened={true}
        onClose={onClose}
        title={
          <div className="flex items-center gap-2">
            <CalendarDays className="w-5 h-5 text-[var(--theme-primary)]" />
            <span>{event ? 'Edit Event' : 'Create Event'}</span>
          </div>
        }
        size="lg"
      >
        <form onSubmit={handleSubmit} className="space-y-4">
          {/* Error */}
          {error && (
            <div
              className="p-3 rounded-lg"
              style={{
                backgroundColor: 'color-mix(in srgb, var(--theme-status-error) 10%, transparent)',
                border: '1px solid color-mix(in srgb, var(--theme-status-error) 30%, transparent)'
              }}
            >
              <p className="text-sm text-[var(--theme-status-error)]">{error}</p>
            </div>
          )}

          {/* Name */}
          <div>
            <label className="block text-sm font-medium text-[var(--theme-text-primary)] mb-1">
              Event Name *
            </label>
            <input
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g., LAN Party 2024"
              className="w-full px-3 py-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] text-[var(--theme-text-primary)] placeholder-[var(--theme-text-secondary)] focus:outline-none focus:border-[var(--theme-primary)]"
              autoFocus
            />
          </div>

          {/* Description */}
          <div>
            <label className="block text-sm font-medium text-[var(--theme-text-primary)] mb-1">
              Description
            </label>
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Optional description..."
              rows={3}
              className="w-full px-3 py-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] text-[var(--theme-text-primary)] placeholder-[var(--theme-text-secondary)] focus:outline-none focus:border-[var(--theme-primary)] resize-none"
            />
          </div>

          {/* Date/Time */}
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-[var(--theme-text-primary)] mb-1">
                Start Date & Time *
              </label>
              <button
                type="button"
                onClick={() => setShowStartPicker(true)}
                className="w-full px-3 py-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] text-[var(--theme-text-primary)] text-left hover:border-[var(--theme-primary)] focus:outline-none focus:border-[var(--theme-primary)] transition-colors flex items-center gap-2"
              >
                <Calendar className="w-4 h-4 text-[var(--theme-text-secondary)] flex-shrink-0" />
                <span className="truncate text-sm">{formatDateTime(startDateTime)}</span>
              </button>
            </div>
            <div>
              <label className="block text-sm font-medium text-[var(--theme-text-primary)] mb-1">
                End Date & Time *
              </label>
              <button
                type="button"
                onClick={() => setShowEndPicker(true)}
                className="w-full px-3 py-2 rounded-lg bg-[var(--theme-bg-tertiary)] border border-[var(--theme-border-primary)] text-[var(--theme-text-primary)] text-left hover:border-[var(--theme-primary)] focus:outline-none focus:border-[var(--theme-primary)] transition-colors flex items-center gap-2"
              >
                <Calendar className="w-4 h-4 text-[var(--theme-text-secondary)] flex-shrink-0" />
                <span className="truncate text-sm">{formatDateTime(endDateTime)}</span>
              </button>
            </div>
          </div>

          {/* Color */}
          <div>
            <label className="block text-sm font-medium text-[var(--theme-text-primary)] mb-2">
              Color
            </label>
            <div className="flex gap-2">
              {eventColors.map((c, index) => {
                const isSelected = color === c || (!color && index === 0);
                return (
                  <button
                    key={`${c}-${index}`}
                    type="button"
                    onClick={() => setColor(c)}
                    className={`w-8 h-8 rounded-lg transition-all flex items-center justify-center ${
                      isSelected ? 'scale-110' : 'hover:scale-105'
                    }`}
                    style={{
                      backgroundColor: c,
                      boxShadow: isSelected
                        ? `0 0 0 2px var(--theme-bg-secondary), 0 0 0 4px var(--theme-primary)`
                        : 'none'
                    }}
                  >
                    {isSelected && <Check className="w-5 h-5 text-white drop-shadow-[0_1px_1px_rgba(0,0,0,0.5)]" />}
                  </button>
                );
              })}
            </div>
          </div>

          {/* Actions */}
          <div className="flex justify-between pt-4 border-t border-[var(--theme-border-primary)]">
            <div>
              {event && (
                <Button
                  type="button"
                  color="red"
                  variant="subtle"
                  onClick={handleDeleteClick}
                  leftSection={<Trash2 className="w-4 h-4" />}
                >
                  Delete
                </Button>
              )}
            </div>
            <div className="flex gap-2">
              <Button
                type="button"
                variant="subtle"
                onClick={onClose}
                disabled={saving || deleting}
              >
                Cancel
              </Button>
              <Button
                type="submit"
                color="blue"
                loading={saving}
              >
                {event ? 'Save Changes' : 'Create Event'}
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
            title="Select Start Date & Time"
          />
        )}

        {/* End DateTime Picker */}
        {showEndPicker && (
          <DateTimePicker
            value={endDateTime}
            onChange={setEndDateTime}
            onClose={() => setShowEndPicker(false)}
            title="Select End Date & Time"
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
            <span>Delete Event</span>
          </div>
        }
        size="sm"
      >
        <div className="space-y-4">
          <p className="text-[var(--theme-text-secondary)]">
            Are you sure you want to delete <strong className="text-[var(--theme-text-primary)]">"{event?.name}"</strong>?
          </p>
          <p className="text-sm text-[var(--theme-text-muted)]">
            This action cannot be undone.
          </p>
          <div className="flex justify-end gap-2 pt-4 border-t border-[var(--theme-border-primary)]">
            <Button
              variant="subtle"
              onClick={() => setShowDeleteConfirm(false)}
              disabled={deleting}
            >
              Cancel
            </Button>
            <Button
              color="red"
              onClick={handleDeleteConfirm}
              loading={deleting}
            >
              Delete Event
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default EventModal;
