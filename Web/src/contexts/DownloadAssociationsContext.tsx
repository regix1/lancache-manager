import React, { createContext, useContext, useState, useCallback, useRef, useEffect, ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext';
import type { TagSummary, EventSummary, Tag } from '../types';

interface DownloadAssociations {
  tags: TagSummary[];
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
  updateTagInCache: (downloadId: number, tag: TagSummary | Tag, action: 'add' | 'remove') => void;
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
      // Fetch associations for each download ID in parallel
      const results = await Promise.all(
        newIds.map(id => ApiService.getDownloadWithAssociations(id))
      );

      const newAssociations: AssociationsCache = {};
      for (const result of results) {
        if (result) {
          fetchedIds.current.add(result.download.id);
          newAssociations[result.download.id] = {
            tags: result.tags.map(t => ({ id: t.id, name: t.name, colorIndex: t.colorIndex })),
            events: result.events.map(e => ({ id: e.id, name: e.name, colorIndex: e.colorIndex, autoTagged: e.autoTagged }))
          };
        }
      }

      setAssociations(prev => ({ ...prev, ...newAssociations }));
    } catch (err) {
      console.error('Failed to fetch download associations:', err);
    } finally {
      setLoading(false);
    }
  }, []);

  const getAssociations = useCallback((downloadId: number): DownloadAssociations => {
    return associations[downloadId] || { tags: [], events: [] };
  }, [associations]);

  const updateTagInCache = useCallback((downloadId: number, tag: TagSummary | Tag, action: 'add' | 'remove') => {
    setAssociations(prev => {
      const current = prev[downloadId] || { tags: [], events: [] };
      const tagSummary: TagSummary = {
        id: tag.id,
        name: tag.name,
        colorIndex: tag.colorIndex
      };

      const newTags = action === 'add'
        ? [...current.tags, tagSummary]
        : current.tags.filter(t => t.id !== tag.id);

      return {
        ...prev,
        [downloadId]: { ...current, tags: newTags }
      };
    });
  }, []);

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

  // Update tag color in all cached associations when a tag is updated
  const updateTagInCacheFromSignalR = useCallback((tag: { id: number; name: string; colorIndex: number }) => {
    setAssociations(prev => {
      const updated: AssociationsCache = {};
      for (const [downloadId, assoc] of Object.entries(prev)) {
        updated[Number(downloadId)] = {
          ...assoc,
          tags: assoc.tags.map((t: TagSummary) =>
            t.id === tag.id ? { ...t, name: tag.name, colorIndex: tag.colorIndex } : t
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

    const handleTagUpdated = (tag: { id: number; name: string; colorIndex: number }) => {
      updateTagInCacheFromSignalR(tag);
    };

    // Clear cache when downloads are refreshed (new downloads may have been auto-tagged)
    const handleDownloadsRefresh = () => {
      // Clear the fetched IDs so downloads will be re-fetched with updated associations
      fetchedIds.current.clear();
    };

    on('EventDeleted', handleEventDeleted);
    on('EventUpdated', handleEventUpdated);
    on('TagUpdated', handleTagUpdated);
    on('DownloadsRefresh', handleDownloadsRefresh);

    return () => {
      off('EventDeleted', handleEventDeleted);
      off('EventUpdated', handleEventUpdated);
      off('TagUpdated', handleTagUpdated);
      off('DownloadsRefresh', handleDownloadsRefresh);
    };
  }, [on, off, removeEventFromCache, updateEventInCache, updateTagInCacheFromSignalR]);

  return (
    <DownloadAssociationsContext.Provider
      value={{
        associations,
        loading,
        fetchAssociations,
        getAssociations,
        updateTagInCache,
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

export default DownloadAssociationsContext;
