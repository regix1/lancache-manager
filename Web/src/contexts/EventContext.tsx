import React, { createContext, useContext, useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import { storage } from '@utils/storage';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/AuthContext';
import { useSignalR } from '@contexts/SignalRContext';
import type { Event, CreateEventRequest, UpdateEventRequest, EventFilterMode, EventDataStackMode } from '../types';

interface EventContextType {
  // Event data
  events: Event[];
  activeEvents: Event[];
  selectedEventId: number | null;
  selectedEvent: Event | null;

  // Filter settings
  filterMode: EventFilterMode;
  dataStackMode: EventDataStackMode;

  // Loading/error states
  loading: boolean;
  error: string | null;

  // Actions
  setSelectedEventId: (id: number | null) => void;
  setFilterMode: (mode: EventFilterMode) => void;
  setDataStackMode: (mode: EventDataStackMode) => void;

  // CRUD operations
  createEvent: (data: CreateEventRequest) => Promise<Event>;
  updateEvent: (id: number, data: UpdateEventRequest) => Promise<Event>;
  deleteEvent: (id: number) => Promise<void>;
  refreshEvents: () => Promise<void>;
}

const EventContext = createContext<EventContextType | undefined>(undefined);

export const useEvents = () => {
  const context = useContext(EventContext);
  if (!context) {
    throw new Error('useEvents must be used within an EventProvider');
  }
  return context;
};

interface EventProviderProps {
  children: ReactNode;
}

export const EventProvider: React.FC<EventProviderProps> = ({ children }) => {
  const { isAuthenticated, authMode, isLoading: authLoading } = useAuth();
  const { on, off } = useSignalR();
  const [events, setEvents] = useState<Event[]>([]);
  const [activeEvents, setActiveEvents] = useState<Event[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const refreshEventsRef = useRef<(() => Promise<void>) | undefined>(undefined);

  // Restore selected event ID from localStorage
  const [selectedEventId, setSelectedEventIdState] = useState<number | null>(() => {
    const saved = storage.getItem('lancache_selected_event_id');
    return saved ? parseInt(saved, 10) : null;
  });

  // Filter mode: 'timeWindow' or 'tagged'
  const [filterMode, setFilterModeState] = useState<EventFilterMode>(() => {
    const saved = storage.getItem('lancache_event_filter_mode');
    return (saved as EventFilterMode) || 'timeWindow';
  });

  // Data stack mode: 'eventOnly' or 'eventAndCurrent'
  const [dataStackMode, setDataStackModeState] = useState<EventDataStackMode>(() => {
    const saved = storage.getItem('lancache_event_data_stack_mode');
    return (saved as EventDataStackMode) || 'eventOnly';
  });

  // Computed: get selected event object
  const selectedEvent = selectedEventId
    ? events.find(e => e.id === selectedEventId) || null
    : null;

  // Persist selected event ID
  const setSelectedEventId = useCallback((id: number | null) => {
    setSelectedEventIdState(id);
    if (id !== null) {
      storage.setItem('lancache_selected_event_id', id.toString());
    } else {
      storage.removeItem('lancache_selected_event_id');
    }
  }, []);

  // Persist filter mode
  const setFilterMode = useCallback((mode: EventFilterMode) => {
    setFilterModeState(mode);
    storage.setItem('lancache_event_filter_mode', mode);
  }, []);

  // Persist data stack mode
  const setDataStackMode = useCallback((mode: EventDataStackMode) => {
    setDataStackModeState(mode);
    storage.setItem('lancache_event_data_stack_mode', mode);
  }, []);

  // Fetch all events
  const refreshEvents = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      // Fetch both endpoints independently so active events can still show for guests
      // even if the full event list is unavailable.
      const [allEventsResult, activeResult] = await Promise.allSettled([
        ApiService.getEvents(),
        ApiService.getActiveEvents()
      ]);

      if (activeResult.status === 'fulfilled') {
        setActiveEvents(activeResult.value);
      } else {
        setActiveEvents([]);
        const message = activeResult.reason instanceof Error ? activeResult.reason.message : 'Failed to fetch active events';
        setError(message);
        console.error('Failed to fetch active events:', activeResult.reason);
      }

      if (allEventsResult.status === 'fulfilled') {
        const allEvents = allEventsResult.value;
        setEvents(allEvents);

        // Clear selected event if it no longer exists
        if (selectedEventId && !allEvents.find(e => e.id === selectedEventId)) {
          setSelectedEventId(null);
        }
      } else if (authMode === 'authenticated') {
        // Guests may not have access to the full event list; avoid surfacing a noisy error in that case.
        const message = allEventsResult.reason instanceof Error ? allEventsResult.reason.message : 'Failed to fetch events';
        setError(prev => prev ?? message);
        console.error('Failed to fetch events:', allEventsResult.reason);
      }
    } finally {
      setLoading(false);
    }
  }, [authMode, selectedEventId, setSelectedEventId]);

  // Initial load - fetch when authenticated or in guest mode
  const hasAccess = isAuthenticated || authMode === 'guest';
  useEffect(() => {
    if (!authLoading && hasAccess) {
      refreshEvents();
    }
  }, [authLoading, hasAccess, refreshEvents]);

  // CRUD operations
  const createEvent = useCallback(async (data: CreateEventRequest): Promise<Event> => {
    const created = await ApiService.createEvent(data);
    await refreshEvents();
    return created;
  }, [refreshEvents]);

  const updateEvent = useCallback(async (id: number, data: UpdateEventRequest): Promise<Event> => {
    const updated = await ApiService.updateEvent(id, data);
    await refreshEvents();
    return updated;
  }, [refreshEvents]);

  const deleteEvent = useCallback(async (id: number): Promise<void> => {
    await ApiService.deleteEvent(id);
    if (selectedEventId === id) {
      setSelectedEventId(null);
    }
    await refreshEvents();
  }, [selectedEventId, setSelectedEventId, refreshEvents]);

  // Keep ref updated for SignalR handlers
  useEffect(() => {
    refreshEventsRef.current = refreshEvents;
  }, [refreshEvents]);

  // Listen for SignalR events
  useEffect(() => {
    const handleEventCreated = () => {
      refreshEventsRef.current?.();
    };

    const handleEventUpdated = () => {
      refreshEventsRef.current?.();
    };

    const handleEventDeleted = () => {
      refreshEventsRef.current?.();
    };

    const handleEventsCleared = () => {
      // All events were cleared via Database Management - refresh to clear the list
      refreshEventsRef.current?.();
    };

    on('EventCreated', handleEventCreated);
    on('EventUpdated', handleEventUpdated);
    on('EventDeleted', handleEventDeleted);
    on('EventsCleared', handleEventsCleared);

    return () => {
      off('EventCreated', handleEventCreated);
      off('EventUpdated', handleEventUpdated);
      off('EventDeleted', handleEventDeleted);
      off('EventsCleared', handleEventsCleared);
    };
  }, [on, off]);

  return (
    <EventContext.Provider
      value={{
        events,
        activeEvents,
        selectedEventId,
        selectedEvent,
        filterMode,
        dataStackMode,
        loading,
        error,
        setSelectedEventId,
        setFilterMode,
        setDataStackMode,
        createEvent,
        updateEvent,
        deleteEvent,
        refreshEvents
      }}
    >
      {children}
    </EventContext.Provider>
  );
};
