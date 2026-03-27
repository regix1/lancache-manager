import React from 'react';
import { X } from 'lucide-react';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import type {
  SessionFilters,
  CacheStatusFilter,
  TimeRangeFilter,
  SessionSortBy
} from './useSessionFilters';

interface SessionFilterBarProps {
  filters: SessionFilters;
  updateFilter: <K extends keyof SessionFilters>(key: K, value: SessionFilters[K]) => void;
  resetFilters: () => void;
  uniqueIps: string[];
  totalCount: number;
  filteredCount: number;
  hasActiveFilters: boolean;
}

const CACHE_STATUS_OPTIONS: { value: CacheStatusFilter; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'cached', label: 'Cached' },
  { value: 'missed', label: 'Missed' },
  { value: 'full', label: 'Full' }
];

const TIME_RANGE_OPTIONS: { value: TimeRangeFilter; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: '1h', label: '1h' },
  { value: '24h', label: '24h' },
  { value: '7d', label: '7d' }
];

const SORT_OPTIONS: { value: SessionSortBy; label: string }[] = [
  { value: 'newest', label: 'Newest' },
  { value: 'oldest', label: 'Oldest' },
  { value: 'largest', label: 'Largest' },
  { value: 'smallest', label: 'Smallest' },
  { value: 'bestCache', label: 'Best Cache' },
  { value: 'worstCache', label: 'Worst Cache' }
];

const SESSIONS_PER_PAGE_OPTIONS: { value: string; label: string }[] = [
  { value: '3', label: '3' },
  { value: '5', label: '5' },
  { value: '10', label: '10' },
  { value: '99999', label: 'All' }
];

const ITEMS_PER_SESSION_OPTIONS: { value: string; label: string }[] = [
  { value: '10', label: '10' },
  { value: '25', label: '25' },
  { value: '50', label: '50' },
  { value: '99999', label: 'All' }
];

const SessionFilterBar: React.FC<SessionFilterBarProps> = ({
  filters,
  updateFilter,
  resetFilters,
  uniqueIps,
  totalCount,
  filteredCount,
  hasActiveFilters
}) => {
  const handleToggleIp = (ip: string): void => {
    const current = filters.clientIps;
    const next = current.includes(ip) ? current.filter((x: string) => x !== ip) : [...current, ip];
    updateFilter('clientIps', next);
  };

  const handleClearCacheStatus = (): void => {
    updateFilter('cacheStatus', 'all');
  };

  const handleClearTimeRange = (): void => {
    updateFilter('timeRange', 'all');
  };

  const ipOptions = uniqueIps.map((ip: string) => ({
    value: ip,
    label: ip
  }));

  const sortDropdownOptions = SORT_OPTIONS.map((opt) => ({
    value: opt.value,
    label: opt.label
  }));

  const sessionsPerPageDropdownOptions = SESSIONS_PER_PAGE_OPTIONS.map((opt) => ({
    value: opt.value,
    label: opt.label
  }));

  const itemsPerSessionDropdownOptions = ITEMS_PER_SESSION_OPTIONS.map((opt) => ({
    value: opt.value,
    label: opt.label
  }));

  const activeChips: React.ReactNode[] = [];

  if (filters.clientIps.length > 0) {
    filters.clientIps.forEach((ip: string) => {
      activeChips.push(
        <span key={`ip-${ip}`} className="session-filter-chip">
          IP: {ip}
          <button
            className="session-filter-chip-remove"
            onClick={() => handleToggleIp(ip)}
            aria-label={`Remove IP filter ${ip}`}
          >
            <X size={10} />
          </button>
        </span>
      );
    });
  }

  if (filters.cacheStatus !== 'all') {
    const label = CACHE_STATUS_OPTIONS.find((o) => o.value === filters.cacheStatus)?.label;
    activeChips.push(
      <span key="cache-status" className="session-filter-chip">
        Cache: {label}
        <button
          className="session-filter-chip-remove"
          onClick={handleClearCacheStatus}
          aria-label="Remove cache status filter"
        >
          <X size={10} />
        </button>
      </span>
    );
  }

  if (filters.timeRange !== 'all') {
    const label = TIME_RANGE_OPTIONS.find((o) => o.value === filters.timeRange)?.label;
    activeChips.push(
      <span key="time-range" className="session-filter-chip">
        Time: {label}
        <button
          className="session-filter-chip-remove"
          onClick={handleClearTimeRange}
          aria-label="Remove time range filter"
        >
          <X size={10} />
        </button>
      </span>
    );
  }

  return (
    <div className="session-filter-bar-wrapper">
      <div className="session-filter-bar">
        {uniqueIps.length > 1 && (
          <div className="session-filter-group">
            <span className="session-filter-label">IP</span>
            <MultiSelectDropdown
              options={ipOptions}
              values={filters.clientIps}
              onChange={(values: string[]) => updateFilter('clientIps', values)}
              placeholder="All IPs"
              minSelections={0}
              className="session-filter-ip-select"
              compactMode
            />
          </div>
        )}

        <div className="session-filter-group">
          <span className="session-filter-label">Cache</span>
          <div className="session-filter-pills">
            {CACHE_STATUS_OPTIONS.map((option) => (
              <button
                key={option.value}
                className={`session-filter-pill${filters.cacheStatus === option.value ? ' active' : ''}`}
                onClick={() => updateFilter('cacheStatus', option.value)}
              >
                {option.label}
              </button>
            ))}
          </div>
        </div>

        <div className="session-filter-group">
          <span className="session-filter-label">Time</span>
          <div className="session-filter-pills">
            {TIME_RANGE_OPTIONS.map((option) => (
              <button
                key={option.value}
                className={`session-filter-pill${filters.timeRange === option.value ? ' active' : ''}`}
                onClick={() => updateFilter('timeRange', option.value)}
              >
                {option.label}
              </button>
            ))}
          </div>
        </div>

        <div className="session-filter-group">
          <span className="session-filter-label">Sort</span>
          <EnhancedDropdown
            options={sortDropdownOptions}
            value={filters.sortBy}
            onChange={(value: string) => updateFilter('sortBy', value as SessionSortBy)}
            compactMode
          />
        </div>

        <div className="session-filter-group">
          <span className="session-filter-label">IPs/page</span>
          <EnhancedDropdown
            options={sessionsPerPageDropdownOptions}
            value={String(filters.sessionsPerPage)}
            onChange={(value: string) => updateFilter('sessionsPerPage', Number(value))}
            compactMode
          />
        </div>

        <div className="session-filter-group">
          <span className="session-filter-label">Items/IP</span>
          <EnhancedDropdown
            options={itemsPerSessionDropdownOptions}
            value={String(filters.itemsPerSession)}
            onChange={(value: string) => updateFilter('itemsPerSession', Number(value))}
            compactMode
          />
        </div>

        {hasActiveFilters && (
          <span className="session-filter-count">
            Showing {filteredCount} of {totalCount}
          </span>
        )}

        {hasActiveFilters && (
          <button className="session-filter-clear" onClick={resetFilters}>
            Clear
          </button>
        )}
      </div>

      {activeChips.length > 0 && <div className="session-filter-chips">{activeChips}</div>}
    </div>
  );
};

export default SessionFilterBar;
