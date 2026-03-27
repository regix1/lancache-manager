import { useState, useMemo } from 'react';
import type { Download } from '../../../types';

export type CacheStatusFilter = 'all' | 'cached' | 'missed' | 'full' | 'evicted';
export type TimeRangeFilter = 'all' | '1h' | '24h' | '7d';
export type SessionSortBy =
  | 'newest'
  | 'oldest'
  | 'largest'
  | 'smallest'
  | 'bestCache'
  | 'worstCache';

export interface SessionFilters {
  clientIps: string[];
  cacheStatus: CacheStatusFilter;
  timeRange: TimeRangeFilter;
  sortBy: SessionSortBy;
  sessionsPerPage: number;
  itemsPerSession: number;
}

export interface UseSessionFiltersReturn {
  filters: SessionFilters;
  updateFilter: <K extends keyof SessionFilters>(key: K, value: SessionFilters[K]) => void;
  resetFilters: () => void;
  filteredDownloads: Download[];
  uniqueIps: string[];
  totalCount: number;
  filteredCount: number;
  hasActiveFilters: boolean;
}

const DEFAULT_FILTERS: SessionFilters = {
  clientIps: [],
  cacheStatus: 'all',
  timeRange: 'all',
  sortBy: 'newest',
  sessionsPerPage: 5,
  itemsPerSession: 10
};

const TIME_RANGE_MS: Record<Exclude<TimeRangeFilter, 'all'>, number> = {
  '1h': 60 * 60 * 1000,
  '24h': 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000
};

function applyFilters(downloads: Download[], filters: SessionFilters): Download[] {
  let result = downloads;

  if (filters.clientIps.length > 0) {
    result = result.filter((d) => filters.clientIps.includes(d.clientIp));
  }

  if (filters.cacheStatus !== 'all') {
    result = result.filter((d) => {
      switch (filters.cacheStatus) {
        case 'cached':
          return d.cacheHitBytes > 0;
        case 'missed':
          return d.cacheHitBytes === 0;
        case 'full':
          return d.cacheHitPercent >= 99.9;
        case 'evicted':
          return d.isEvicted === true;
        default:
          return true;
      }
    });
  }

  if (filters.timeRange !== 'all') {
    const cutoff = Date.now() - TIME_RANGE_MS[filters.timeRange];
    result = result.filter((d) => new Date(d.startTimeUtc).getTime() >= cutoff);
  }

  const sorted = [...result];
  switch (filters.sortBy) {
    case 'newest':
      sorted.sort(
        (a, b) => new Date(b.startTimeUtc).getTime() - new Date(a.startTimeUtc).getTime()
      );
      break;
    case 'oldest':
      sorted.sort(
        (a, b) => new Date(a.startTimeUtc).getTime() - new Date(b.startTimeUtc).getTime()
      );
      break;
    case 'largest':
      sorted.sort((a, b) => b.totalBytes - a.totalBytes);
      break;
    case 'smallest':
      sorted.sort((a, b) => a.totalBytes - b.totalBytes);
      break;
    case 'bestCache':
      sorted.sort((a, b) => b.cacheHitPercent - a.cacheHitPercent);
      break;
    case 'worstCache':
      sorted.sort((a, b) => a.cacheHitPercent - b.cacheHitPercent);
      break;
  }

  return sorted;
}

function deriveUniqueIps(downloads: Download[]): string[] {
  const ips = new Set<string>();
  for (const d of downloads) {
    if (d.clientIp) {
      ips.add(d.clientIp);
    }
  }
  return Array.from(ips).sort();
}

function isDefaultFilters(filters: SessionFilters): boolean {
  return (
    filters.clientIps.length === 0 &&
    filters.cacheStatus === DEFAULT_FILTERS.cacheStatus &&
    filters.timeRange === DEFAULT_FILTERS.timeRange &&
    filters.sortBy === DEFAULT_FILTERS.sortBy &&
    filters.sessionsPerPage === DEFAULT_FILTERS.sessionsPerPage &&
    filters.itemsPerSession === DEFAULT_FILTERS.itemsPerSession
  );
}

export function useSessionFilters(downloads: Download[]): UseSessionFiltersReturn {
  const [filters, setFilters] = useState<SessionFilters>(DEFAULT_FILTERS);

  const uniqueIps = useMemo(() => deriveUniqueIps(downloads), [downloads]);

  const filteredDownloads = useMemo(() => applyFilters(downloads, filters), [downloads, filters]);

  const updateFilter = <K extends keyof SessionFilters>(key: K, value: SessionFilters[K]): void => {
    setFilters((prev) => ({ ...prev, [key]: value }));
  };

  const resetFilters = (): void => {
    setFilters(DEFAULT_FILTERS);
  };

  const hasActiveFilters = !isDefaultFilters(filters);

  return {
    filters,
    updateFilter,
    resetFilters,
    filteredDownloads,
    uniqueIps,
    totalCount: downloads.length,
    filteredCount: filteredDownloads.length,
    hasActiveFilters
  };
}
