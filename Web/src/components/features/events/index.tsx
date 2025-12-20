import React, { useState, useCallback } from 'react';
import { CalendarDays, Plus, List, Grid, Loader2 } from 'lucide-react';
import { useEvents } from '@contexts/EventContext';
import { Button } from '@components/ui/Button';
import EventCalendar from './EventCalendar';
import EventModal from './EventModal';
import EventList from './EventList';
import type { Event } from '../../../types';

type ViewMode = 'calendar' | 'list';

const EventsTab: React.FC = () => {
  const { events, activeEvents, loading, error, refreshEvents } = useEvents();
  const [viewMode, setViewMode] = useState<ViewMode>('calendar');
  const [showCreateModal, setShowCreateModal] = useState(false);
  const [editingEvent, setEditingEvent] = useState<Event | null>(null);

  const handleCreateEvent = useCallback(() => {
    setEditingEvent(null);
    setShowCreateModal(true);
  }, []);

  const handleEditEvent = useCallback((event: Event) => {
    setEditingEvent(event);
    setShowCreateModal(true);
  }, []);

  const handleCloseModal = useCallback(() => {
    setShowCreateModal(false);
    setEditingEvent(null);
  }, []);

  const handleEventSaved = useCallback(() => {
    handleCloseModal();
    refreshEvents();
  }, [handleCloseModal, refreshEvents]);

  if (loading && events.length === 0) {
    return (
      <div className="flex items-center justify-center h-64">
        <Loader2 className="w-8 h-8 animate-spin text-[var(--theme-primary)]" />
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fadeIn">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <CalendarDays className="w-6 h-6 text-[var(--theme-primary)]" />
          <div>
            <h2 className="text-xl font-semibold text-[var(--theme-text-primary)]">Events</h2>
            <p className="text-sm text-[var(--theme-text-secondary)]">
              Schedule and manage LAN events. Track downloads during events.
            </p>
          </div>
        </div>

        <div className="flex items-center gap-2">
          {/* View Toggle */}
          <div className="flex rounded-lg overflow-hidden border border-[var(--theme-border-primary)]">
            <button
              onClick={() => setViewMode('calendar')}
              className={`p-2 transition-colors ${
                viewMode === 'calendar'
                  ? 'bg-[var(--theme-primary)] text-[var(--theme-button-text)]'
                  : 'bg-[var(--theme-bg-secondary)] text-[var(--theme-text-secondary)] hover:text-[var(--theme-text-primary)]'
              }`}
              title="Calendar view"
            >
              <Grid className="w-4 h-4" />
            </button>
            <button
              onClick={() => setViewMode('list')}
              className={`p-2 transition-colors ${
                viewMode === 'list'
                  ? 'bg-[var(--theme-primary)] text-[var(--theme-button-text)]'
                  : 'bg-[var(--theme-bg-secondary)] text-[var(--theme-text-secondary)] hover:text-[var(--theme-text-primary)]'
              }`}
              title="List view"
            >
              <List className="w-4 h-4" />
            </button>
          </div>

          {/* Create Event Button */}
          <Button
            onClick={handleCreateEvent}
            color="blue"
            leftSection={<Plus className="w-4 h-4" />}
          >
            New Event
          </Button>
        </div>
      </div>

      {/* Error message */}
      {error && (
        <div
          className="p-4 rounded-lg"
          style={{
            backgroundColor: 'color-mix(in srgb, var(--theme-status-error) 10%, transparent)',
            border: '1px solid color-mix(in srgb, var(--theme-status-error) 30%, transparent)'
          }}
        >
          <p className="text-sm text-[var(--theme-status-error)]">{error}</p>
        </div>
      )}

      {/* Active Events Banner */}
      {activeEvents.length > 0 && (
        <div
          className="p-4 rounded-lg bg-[var(--theme-bg-tertiary)]"
          style={{ border: '1px solid var(--theme-status-success)' }}
        >
          <div className="flex items-center gap-2">
            <div className="w-2 h-2 rounded-full bg-[var(--theme-status-success)] animate-pulse" />
            <span className="text-sm font-medium text-[var(--theme-status-success)]">
              {activeEvents.length} active event{activeEvents.length > 1 ? 's' : ''} in progress
            </span>
          </div>
          <div className="mt-2 flex flex-wrap gap-2">
            {activeEvents.map(event => (
              <button
                key={event.id}
                onClick={() => handleEditEvent(event)}
                className="px-3 py-1 text-sm rounded-lg transition-colors"
                style={{
                  backgroundColor: `${event.color}20`,
                  color: event.color,
                  border: `1px solid ${event.color}40`
                }}
              >
                {event.name}
              </button>
            ))}
          </div>
        </div>
      )}

      {/* Main Content */}
      <div className="themed-card rounded-lg p-4">
        {viewMode === 'calendar' ? (
          <EventCalendar
            events={events}
            onEventClick={handleEditEvent}
            onDayClick={() => {
              setEditingEvent(null);
              setShowCreateModal(true);
            }}
          />
        ) : (
          <EventList
            events={events}
            onEventClick={handleEditEvent}
          />
        )}
      </div>

      {/* Create/Edit Modal */}
      {showCreateModal && (
        <EventModal
          event={editingEvent}
          onClose={handleCloseModal}
          onSave={handleEventSaved}
        />
      )}
    </div>
  );
};

export default EventsTab;
