import React, { createContext, useContext, useState, useCallback, useRef, useEffect, ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext';
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
}

const DownloadAssociationsContext = createContext<DownloadAssociationsContextType | undefined>(undefined);

interface DownloadAssociationsProviderProps {
  children: ReactNode;
}

export const DownloadAssociationsProvider: React.FC<DownloadAssociationsProviderProps> = ({ children }) => {
  const { on, off } = useSignalR();
  const [associations, setAssociations] = useState<AssociationsCache>({});
  const [loading, setLoading] = useState(false);
  const fetchedIds = useRef<Set<number>>(new Set());

  const fetchAssociations = useCallback(async (downloadIds: number[]) => {
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

    // Clear cache when downloads are refreshed (new downloads may have been auto-tagged)
    const handleDownloadsRefresh = () => {
      // Clear the fetched IDs so downloads will be re-fetched with updated associations
      fetchedIds.current.clear();
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
    on('DownloadsRefresh', handleDownloadsRefresh);
    on('EventsCleared', handleEventsCleared);

    return () => {
      off('EventDeleted', handleEventDeleted);
      off('EventUpdated', handleEventUpdated);
      off('DownloadsRefresh', handleDownloadsRefresh);
      off('EventsCleared', handleEventsCleared);
    };
  }, [on, off, removeEventFromCache, updateEventInCache]);

  return (
    <DownloadAssociationsContext.Provider
      value={{
        associations,
        loading,
        fetchAssociations,
        getAssociations,
        clearCache
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
