import React, { useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import { storage } from '@utils/storage';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useTimeFilter } from '@contexts/useTimeFilter';
import type {
  Event,
  CreateEventRequest,
  UpdateEventRequest,
  EventFilterMode,
  EventDataStackMode
} from '../types';
import { EventContext } from './EventContext.types';
import { pruneMissingEventIds } from './TimeFilterContext.utils';

interface EventProviderProps {
  children: ReactNode;
}

export const EventProvider: React.FC<EventProviderProps> = ({ children }) => {
  const { hasSession, authMode, sessionId, isLoading: authLoading } = useAuth();
  const { on, off } = useSignalR();
  const { selectedEventIds, setSelectedEventIds, timeRange, setTimeRange } = useTimeFilter();
  const [events, setEvents] = useState<Event[]>([]);
  const [activeEvents, setActiveEvents] = useState<Event[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const refreshEventsRef = useRef<(() => Promise<void>) | undefined>(undefined);
  // Monotonic id claimed by each refreshEvents call. Only the call still holding the latest id
  // when its response lands may write any of this context's state - see the guard in refreshEvents.
  const refreshRequestIdRef = useRef(0);
  const hasLoadedRef = useRef(false);

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
    ? events.find((e) => e.id === selectedEventId) || null
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
    // Claim the latest request id up front. Several call sites can overlap (SignalR handlers
    // fire refreshEvents without awaiting, the mount effect re-runs whenever this callback's
    // identity changes), so an older call can resolve after a newer one already updated the
    // list. Without this guard that stale write used to delete a selection the newer list
    // still supports (the prune effect below then also removes that id from
    // `selectedEventIds` once it re-checks against the newer `events`).
    const requestId = ++refreshRequestIdRef.current;
    if (!hasLoadedRef.current) {
      setLoading(true);
    }
    setError(null);
    try {
      // Fetch both endpoints independently so active events can still show for guests
      // even if the full event list is unavailable.
      const [allEventsResult, activeResult] = await Promise.allSettled([
        ApiService.getEvents(),
        ApiService.getActiveEvents()
      ]);

      // A newer call claimed the id while this one was in flight. Both lists come from the same
      // settled pair, so writing any single field now would splice a stale snapshot into the fresh
      // one: the newer call's `events` beside this call's `activeEvents`, or this call's failure
      // message on top of the newer call's success, with nothing scheduled to correct it. Skip
      // every write and let the newer call own the whole result.
      if (requestId !== refreshRequestIdRef.current) {
        return;
      }

      if (activeResult.status === 'fulfilled') {
        setActiveEvents(activeResult.value);
      } else {
        setActiveEvents([]);
        const message =
          activeResult.reason instanceof Error
            ? activeResult.reason.message
            : 'Failed to fetch active events';
        setError(message);
        console.error('Failed to fetch active events:', activeResult.reason);
      }

      if (allEventsResult.status === 'fulfilled') {
        const allEvents = allEventsResult.value;
        setEvents(allEvents);

        // Clear selected event if it no longer exists
        if (selectedEventId && !allEvents.find((e) => e.id === selectedEventId)) {
          setSelectedEventId(null);
        }
      } else {
        // The full list is the privileged half of the pair. Leaving the previous value in place is
        // what kept an administrator's events on screen for the guest who replaced them, because a
        // guest's fetch of this endpoint is expected to fail, so the failure empties it whoever
        // asked. Only an authenticated reader is told about it; for a guest the rejection is the
        // normal answer and not worth an error banner. [6]
        setEvents([]);
        if (authMode === 'authenticated') {
          const message =
            allEventsResult.reason instanceof Error
              ? allEventsResult.reason.message
              : 'Failed to fetch events';
          setError((prev) => prev ?? message);
          console.error('Failed to fetch events:', allEventsResult.reason);
        }
      }
    } finally {
      // Only the call still holding the latest id clears the flag. A superseded call finishing
      // first would hide the initial loading state while the newer refresh is still running.
      if (requestId === refreshRequestIdRef.current) {
        hasLoadedRef.current = true;
        setLoading(false);
      }
    }
  }, [authMode, selectedEventId, setSelectedEventId]);

  // Initial load - fetch when authenticated or in guest mode
  const hasAccess = hasSession;

  // Which session the state above belongs to. Two guest sessions are as different as a guest and an
  // administrator here, so the id is part of it: a swap between sessions of the same kind moves
  // neither authMode nor hasAccess, and refreshEvents keeps its identity, so nothing else would
  // notice the handoff.
  const sessionIdentity = `${authMode}:${sessionId ?? ''}`;
  const loadedIdentityRef = useRef(sessionIdentity);

  useEffect(() => {
    if (loadedIdentityRef.current !== sessionIdentity) {
      loadedIdentityRef.current = sessionIdentity;
      // Claiming a fresh id first is what retires the previous session's in-flight calls: they
      // fail the guard in refreshEvents when they land and write nothing. Without the reset the
      // provider keeps serving whatever the last session loaded, and hasLoadedRef keeps the loader
      // suppressed for a session that has fetched nothing yet. [6]
      refreshRequestIdRef.current++;
      hasLoadedRef.current = false;
      setEvents([]);
      setActiveEvents([]);
      setError(null);
      // A session that can still fetch goes back to the state the first load had; one that cannot,
      // which is what a logout leaves behind, settles as empty instead of spinning forever.
      setLoading(authLoading || hasAccess);
    }

    if (!authLoading && hasAccess) {
      refreshEvents();
    }
  }, [authLoading, hasAccess, sessionIdentity, refreshEvents]);

  // Drop dashboard-filter event ids whose event no longer exists - covers deleteEvent, the
  // EventDeleted/EventsCleared SignalR handlers, any other path that shrinks `events`, and a
  // user ticking an id that a failed refresh left stale in the picker, since all of them go
  // through this same state. Only when the prune leaves nothing selected does the dashboard
  // return to the normal live view; if other selected events survive, the chosen time range
  // still applies to them and must be left alone. Watches `selectedEventIds` as well as `events`
  // so a newly-ticked stale id is re-checked immediately rather than waiting on the next events
  // refresh; this cannot loop because pruneMissingEventIds returns the same array reference when
  // nothing is removed, so a no-op prune never reaches setSelectedEventIds.
  useEffect(() => {
    const pruned = pruneMissingEventIds(selectedEventIds, events);
    if (pruned !== selectedEventIds) {
      setSelectedEventIds(pruned);
      if (pruned.length === 0 && timeRange !== 'live') {
        setTimeRange('live');
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [events, selectedEventIds]);

  // CRUD operations
  const createEvent = useCallback(
    async (data: CreateEventRequest): Promise<Event> => {
      const created = await ApiService.createEvent(data);
      await refreshEvents();
      return created;
    },
    [refreshEvents]
  );

  const updateEvent = useCallback(
    async (id: number, data: UpdateEventRequest): Promise<Event> => {
      const updated = await ApiService.updateEvent(id, data);
      await refreshEvents();
      return updated;
    },
    [refreshEvents]
  );

  const deleteEvent = useCallback(
    async (id: number): Promise<void> => {
      await ApiService.deleteEvent(id);
      if (selectedEventId === id) {
        setSelectedEventId(null);
      }
      await refreshEvents();
    },
    [selectedEventId, setSelectedEventId, refreshEvents]
  );

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
