import React, { createContext, useContext, useState, useCallback, useRef, useEffect, ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext';
import { useAuth } from '@contexts/AuthContext';
import type { EventSummary } from '../types';

interface DownloadAssociations {
  events: EventSummary[];
}

interface AssociationsCache {
  [downloadId: number]: DownloadAssociations;
}

interface DownloadAssociationsContextType {
  associations: AssociationsCache;
  loading: boolean;
  fetchAssociations: (downloadIds: number[]) => Promise<void>;
  getAssociations: (downloadId: number) => DownloadAssociations;
  clearCache: () => void;
  /** Increments when cache is invalidated - include in useEffect deps to trigger re-fetch */
  refreshVersion: number;
}

const DownloadAssociationsContext = createContext<DownloadAssociationsContextType | undefined>(undefined);

interface DownloadAssociationsProviderProps {
  children: ReactNode;
}

export const DownloadAssociationsProvider: React.FC<DownloadAssociationsProviderProps> = ({ children }) => {
  const { on, off } = useSignalR();
  const { authMode } = useAuth();
  const isAdmin = authMode === 'authenticated';
  const [associations, setAssociations] = useState<AssociationsCache>({});
  const [loading, setLoading] = useState(false);
  const [refreshVersion, setRefreshVersion] = useState(0);
  const fetchedIds = useRef<Set<number>>(new Set());
  const refreshDebounceRef = useRef<NodeJS.Timeout | null>(null);
  const lastRefreshTimeRef = useRef<number>(0);

  const isAdminRef = useRef(isAdmin);
  isAdminRef.current = isAdmin;

  const fetchAssociations = useCallback(async (downloadIds: number[]) => {
    // Batch download events endpoint is admin-only
    if (!isAdminRef.current) return;

    // Filter out already fetched IDs
    const newIds = downloadIds.filter(id => !fetchedIds.current.has(id));
    if (newIds.length === 0) return;

    setLoading(true);
    try {
      // Use batch endpoint - single API call for all IDs
      const results = await ApiService.getBatchDownloadEvents(newIds);

      const newAssociations: AssociationsCache = {};
      for (const [idStr, data] of Object.entries(results)) {
        const id = Number(idStr);
        fetchedIds.current.add(id);
        newAssociations[id] = {
          events: data.events.map(e => ({ id: e.id, name: e.name, colorIndex: e.colorIndex, autoTagged: e.autoTagged }))
        };
      }

      setAssociations(prev => ({ ...prev, ...newAssociations }));
    } catch (err) {
      console.error('Failed to fetch download associations:', err);
    } finally {
      setLoading(false);
    }
  }, []);

  const getAssociations = useCallback((downloadId: number): DownloadAssociations => {
    return associations[downloadId] || { events: [] };
  }, [associations]);

  const clearCache = useCallback(() => {
    setAssociations({});
    fetchedIds.current.clear();
  }, []);

  // Remove a specific event from all cached associations
  const removeEventFromCache = useCallback((eventId: number) => {
    setAssociations(prev => {
      const updated: AssociationsCache = {};
      for (const [downloadId, assoc] of Object.entries(prev)) {
        updated[Number(downloadId)] = {
          ...assoc,
          events: assoc.events.filter((e: EventSummary) => e.id !== eventId)
        };
      }
      return updated;
    });
  }, []);

  // Update event color in all cached associations when an event is updated
  const updateEventInCache = useCallback((event: { id: number; name: string; colorIndex: number }) => {
    setAssociations(prev => {
      const updated: AssociationsCache = {};
      for (const [downloadId, assoc] of Object.entries(prev)) {
        updated[Number(downloadId)] = {
          ...assoc,
          events: assoc.events.map((e: EventSummary) =>
            e.id === event.id ? { ...e, name: event.name, colorIndex: event.colorIndex } : e
          )
        };
      }
      return updated;
    });
  }, []);

  // Listen for SignalR events to keep cache in sync
  useEffect(() => {
    const handleEventDeleted = (eventId: number) => {
      removeEventFromCache(eventId);
    };

    const handleEventUpdated = (event: { id: number; name: string; colorIndex: number }) => {
      updateEventInCache(event);
    };

    // Handle when a download is tagged to an event - invalidate that download's cache
    const handleDownloadTagged = ({ downloadId }: { eventId: number; downloadId: number }) => {
      // Remove from fetchedIds so it will be re-fetched
      fetchedIds.current.delete(downloadId);
      // Remove from associations cache - this triggers a state change
      setAssociations(prev => {
        const updated = { ...prev };
        delete updated[downloadId];
        return updated;
      });
      // Increment refresh version to trigger re-fetch in components
      setRefreshVersion(v => v + 1);
    };

    // Clear cache when downloads are refreshed (new downloads may have been auto-tagged)
    // Debounced to prevent rapid re-renders when multiple DownloadsRefresh events fire
    const handleDownloadsRefresh = () => {
      // Always clear fetchedIds immediately so new fetches will work
      fetchedIds.current.clear();

      // Debounce the refresh version increment to prevent flickering
      // Only trigger re-fetch if 500ms has passed since last refresh
      const now = Date.now();
      if (now - lastRefreshTimeRef.current < 500) {
        // Too soon - schedule a delayed refresh instead
        if (refreshDebounceRef.current) {
          clearTimeout(refreshDebounceRef.current);
        }
        refreshDebounceRef.current = setTimeout(() => {
          lastRefreshTimeRef.current = Date.now();
          setRefreshVersion(v => v + 1);
          refreshDebounceRef.current = null;
        }, 500);
      } else {
        // Enough time has passed - refresh immediately
        lastRefreshTimeRef.current = now;
        setRefreshVersion(v => v + 1);
      }
    };

    // Clear all event associations when events table is cleared
    const handleEventsCleared = () => {
      setAssociations(prev => {
        const updated: AssociationsCache = {};
        for (const [downloadId, assoc] of Object.entries(prev)) {
          updated[Number(downloadId)] = {
            ...assoc,
            events: [] // Clear all events
          };
        }
        return updated;
      });
    };

    on('EventDeleted', handleEventDeleted);
    on('EventUpdated', handleEventUpdated);
    on('DownloadTagged', handleDownloadTagged);
    on('DownloadsRefresh', handleDownloadsRefresh);
    on('EventsCleared', handleEventsCleared);

    return () => {
      off('EventDeleted', handleEventDeleted);
      off('EventUpdated', handleEventUpdated);
      off('DownloadTagged', handleDownloadTagged);
      off('DownloadsRefresh', handleDownloadsRefresh);
      off('EventsCleared', handleEventsCleared);
      // Clear any pending debounce timeout
      if (refreshDebounceRef.current) {
        clearTimeout(refreshDebounceRef.current);
      }
    };
  }, [on, off, removeEventFromCache, updateEventInCache]);

  return (
    <DownloadAssociationsContext.Provider
      value={{
        associations,
        loading,
        fetchAssociations,
        getAssociations,
        clearCache,
        refreshVersion
      }}
    >
      {children}
    </DownloadAssociationsContext.Provider>
  );
};

export const useDownloadAssociations = (): DownloadAssociationsContextType => {
  const context = useContext(DownloadAssociationsContext);
  if (context === undefined) {
    throw new Error('useDownloadAssociations must be used within a DownloadAssociationsProvider');
  }
  return context;
};
