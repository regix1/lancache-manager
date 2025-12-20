import React, { createContext, useContext, useState, useCallback, useRef, ReactNode } from 'react';
import ApiService from '@services/api.service';
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
            tags: result.tags,
            events: result.events
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
        color: tag.color
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
