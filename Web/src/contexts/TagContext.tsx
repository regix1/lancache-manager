import React, { createContext, useContext, useState, useEffect, useCallback, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/AuthContext';
import type { Tag, CreateTagRequest, UpdateTagRequest } from '../types';

interface TagContextType {
  // Tag data
  tags: Tag[];
  loading: boolean;
  error: string | null;

  // CRUD operations
  createTag: (data: CreateTagRequest) => Promise<Tag>;
  updateTag: (id: number, data: UpdateTagRequest) => Promise<Tag>;
  deleteTag: (id: number) => Promise<void>;
  refreshTags: () => Promise<void>;

  // Tag-Download operations (read-only - tags are auto-assigned)
  getTagsForDownload: (downloadId: number) => Promise<Tag[]>;

  // Helper to get tag by ID
  getTagById: (id: number) => Tag | undefined;
}

const TagContext = createContext<TagContextType | undefined>(undefined);

export const useTags = () => {
  const context = useContext(TagContext);
  if (!context) {
    throw new Error('useTags must be used within a TagProvider');
  }
  return context;
};

interface TagProviderProps {
  children: ReactNode;
}

export const TagProvider: React.FC<TagProviderProps> = ({ children }) => {
  const { isAuthenticated, isLoading: authLoading } = useAuth();
  const [tags, setTags] = useState<Tag[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Fetch all tags
  const refreshTags = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const allTags = await ApiService.getTags();
      setTags(allTags);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to fetch tags';
      setError(message);
      console.error('Failed to fetch tags:', err);
    } finally {
      setLoading(false);
    }
  }, []);

  // Initial load - only fetch when authenticated
  useEffect(() => {
    if (!authLoading && isAuthenticated) {
      refreshTags();
    }
  }, [authLoading, isAuthenticated, refreshTags]);

  // CRUD operations
  const createTag = useCallback(async (data: CreateTagRequest): Promise<Tag> => {
    const created = await ApiService.createTag(data);
    await refreshTags();
    return created;
  }, [refreshTags]);

  const updateTag = useCallback(async (id: number, data: UpdateTagRequest): Promise<Tag> => {
    const updated = await ApiService.updateTag(id, data);
    await refreshTags();
    return updated;
  }, [refreshTags]);

  const deleteTag = useCallback(async (id: number): Promise<void> => {
    await ApiService.deleteTag(id);
    await refreshTags();
  }, [refreshTags]);

  // Tag-Download operations (read-only - tags are auto-assigned)
  const getTagsForDownload = useCallback(async (downloadId: number): Promise<Tag[]> => {
    return await ApiService.getTagsForDownload(downloadId);
  }, []);

  // Helper to get tag by ID
  const getTagById = useCallback((id: number): Tag | undefined => {
    return tags.find(t => t.id === id);
  }, [tags]);

  return (
    <TagContext.Provider
      value={{
        tags,
        loading,
        error,
        createTag,
        updateTag,
        deleteTag,
        refreshTags,
        getTagsForDownload,
        getTagById
      }}
    >
      {children}
    </TagContext.Provider>
  );
};
