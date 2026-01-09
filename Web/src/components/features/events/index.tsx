import React, { useState, useCallback } from 'react';
import { CalendarDays, Plus, List, LayoutGrid, Loader2, Sparkles } from 'lucide-react';
import { useEvents } from '@contexts/EventContext';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { getEventColorStyles, getEventColorVar } from '@utils/eventColors';
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
          {/* View Toggle */}
          <SegmentedControl
            options={[
              { value: 'calendar', label: 'Calendar', icon: <LayoutGrid className="w-4 h-4" /> },
              { value: 'list', label: 'List', icon: <List className="w-4 h-4" /> }
            ]}
            value={viewMode}
            onChange={(value) => setViewMode(value as ViewMode)}
            size="md"
            showLabels="responsive"
          />

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
        <div className="p-4 rounded-lg animate-fadeIn bg-[color-mix(in_srgb,var(--theme-status-error)_10%,transparent)] border border-[color-mix(in_srgb,var(--theme-status-error)_30%,transparent)]">
          <p className="text-sm text-[var(--theme-status-error)]">{error}</p>
        </div>
      )}

      {/* Active Events Banner */}
      {activeEvents.length > 0 && (
        <Card padding="md" glassmorphism>
          <div className="flex items-start gap-3">
            <div className="w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 bg-[color-mix(in_srgb,var(--theme-status-success)_15%,transparent)]">
              <Sparkles className="w-4 h-4 text-[var(--theme-status-success)]" />
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
                    style={getEventColorStyles(event.colorIndex)}
                  >
                    <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ backgroundColor: getEventColorVar(event.colorIndex) }} />
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
