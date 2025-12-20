import React, { useState, useCallback } from 'react';
import { CalendarDays, Plus, List, LayoutGrid, Loader2, Sparkles } from 'lucide-react';
import { useEvents } from '@contexts/EventContext';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
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
        <div className="flex flex-col items-center gap-3">
          <Loader2 className="w-8 h-8 animate-spin text-[var(--theme-primary)]" />
          <span className="text-sm text-[var(--theme-text-secondary)]">Loading events...</span>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-6 animate-fadeIn">
      {/* Header Section */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-cyan">
            <CalendarDays className="w-5 h-5 icon-cyan" />
          </div>
          <div>
            <h2 className="text-xl font-semibold text-[var(--theme-text-primary)]">Events</h2>
            <p className="text-sm text-[var(--theme-text-secondary)]">
              Schedule and manage LAN events. Track downloads during events.
            </p>
          </div>
        </div>

        <div className="flex items-center gap-3">
          {/* View Toggle - Styled Segmented Control */}
          <div
            className="flex rounded-lg p-0.5"
            style={{
              backgroundColor: 'var(--theme-bg-tertiary)',
              border: '1px solid var(--theme-border-primary)'
            }}
          >
            <button
              onClick={() => setViewMode('calendar')}
              className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium transition-all duration-200"
              style={{
                backgroundColor: viewMode === 'calendar' ? 'var(--theme-primary)' : 'transparent',
                color: viewMode === 'calendar' ? 'var(--theme-button-text)' : 'var(--theme-text-secondary)'
              }}
            >
              <LayoutGrid className="w-4 h-4" />
              <span className="hidden sm:inline">Calendar</span>
            </button>
            <button
              onClick={() => setViewMode('list')}
              className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-sm font-medium transition-all duration-200"
              style={{
                backgroundColor: viewMode === 'list' ? 'var(--theme-primary)' : 'transparent',
                color: viewMode === 'list' ? 'var(--theme-button-text)' : 'var(--theme-text-secondary)'
              }}
            >
              <List className="w-4 h-4" />
              <span className="hidden sm:inline">List</span>
            </button>
          </div>

          {/* Create Event Button */}
          <Button
            onClick={handleCreateEvent}
            color="blue"
            variant="filled"
            leftSection={<Plus className="w-4 h-4" />}
          >
            <span className="hidden sm:inline">New Event</span>
            <span className="sm:hidden">New</span>
          </Button>
        </div>
      </div>

      {/* Error message */}
      {error && (
        <div
          className="p-4 rounded-lg animate-fadeIn"
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
        <Card padding="md" glassmorphism>
          <div className="flex items-start gap-3">
            <div
              className="w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0"
              style={{ backgroundColor: 'color-mix(in srgb, var(--theme-status-success) 15%, transparent)' }}
            >
              <Sparkles className="w-4 h-4" style={{ color: 'var(--theme-status-success)' }} />
            </div>
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2 mb-2">
                <div className="w-2 h-2 rounded-full bg-[var(--theme-status-success)] animate-pulse" />
                <span className="text-sm font-medium text-[var(--theme-status-success)]">
                  {activeEvents.length} active event{activeEvents.length > 1 ? 's' : ''} in progress
                </span>
              </div>
              <div className="flex flex-wrap gap-2">
                {activeEvents.map(event => (
                  <button
                    key={event.id}
                    onClick={() => handleEditEvent(event)}
                    className="inline-flex items-center gap-1.5 px-2.5 py-1 text-sm rounded-full font-medium transition-all hover:scale-105"
                    style={{
                      backgroundColor: `${event.color}20`,
                      color: event.color,
                      border: `1px solid ${event.color}40`
                    }}
                  >
                    <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ backgroundColor: event.color }} />
                    {event.name}
                  </button>
                ))}
              </div>
            </div>
          </div>
        </Card>
      )}

      {/* Main Content */}
      <Card padding="lg">
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
      </Card>

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
