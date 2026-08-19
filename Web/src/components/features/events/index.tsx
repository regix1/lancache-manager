import React, { useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Plus, List, LayoutGrid } from 'lucide-react';
import { useEvents } from '@contexts/useEvents';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { LoadingState } from '@components/ui/ManagerCard';
import { getColorTierVar, getEventColorVar } from '@utils/eventColors';
import EventCalendar from './EventCalendar';
import EventModal from './EventModal';
import EventList from './EventList';
import type { Event } from '../../../types';

type ViewMode = 'calendar' | 'list';

const EventsTab: React.FC = () => {
  const { t } = useTranslation();
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
      <div className="space-y-6 animate-fadeIn">
        <LoadingState shape="calendar" />
      </div>
    );
  }

  return (
    <div className="space-y-6 animate-fadeIn">
      {/* Header Section */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-end gap-4">
        <div className="flex items-center gap-3">
          {/* View Toggle */}
          <SegmentedControl
            options={[
              {
                value: 'calendar',
                label: 'Calendar',
                icon: <LayoutGrid className="w-4 h-4" />
              },
              {
                value: 'list',
                label: 'List',
                icon: <List className="w-4 h-4" />
              }
            ]}
            value={viewMode}
            onChange={(value) => setViewMode(value as ViewMode)}
            activeColor="neutral"
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
        <div className="p-4 rounded-lg animate-fadeIn bg-[var(--theme-error-faint)] border border-[var(--theme-error-strong)]">
          <p className="text-sm text-[var(--theme-status-error)]">{error}</p>
        </div>
      )}

      {/* Active Events. The count and its chips sit on the shared quiet well rather than on the
          page background - one line of text and a chip with no container of their own read as
          stray copy. The well carries no colour of its own, so the chips stay the only
          saturated thing on the row. */}
      {activeEvents.length > 0 && (
        <div className="well-surface flex flex-wrap items-center gap-2 px-3 py-2">
          <span className="text-sm text-themed-secondary">
            {t('events.activeCount', { count: activeEvents.length })}
          </span>
          {activeEvents.map((event) => {
            const eventColor = getEventColorVar(event.colorIndex);
            return (
              <button
                key={event.id}
                onClick={() => handleEditEvent(event)}
                className="event-active-chip inline-flex items-center gap-1.5 px-2.5 py-1 text-sm themed-border-radius-sm font-medium transition hover:scale-105"
                style={
                  {
                    '--event-chip-color': eventColor,
                    '--event-chip-bg': getColorTierVar(eventColor, 'muted')
                  } as React.CSSProperties
                }
              >
                {event.name}
              </button>
            );
          })}
        </div>
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
          <EventList events={events} onEventClick={handleEditEvent} />
        )}
      </Card>

      {/* Create/Edit Modal */}
      {showCreateModal && (
        <EventModal event={editingEvent} onClose={handleCloseModal} onSave={handleEventSaved} />
      )}
    </div>
  );
};

export default EventsTab;
