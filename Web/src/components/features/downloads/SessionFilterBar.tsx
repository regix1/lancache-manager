import React from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { IpChip } from '@components/ui/IpChip';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { TogglePill } from '@components/ui/TogglePill';
import { useClientGroups } from '@contexts/useClientGroups';
import { useClientHostnames } from '@contexts/useClientHostnames';
import { resolveClientLabel } from '@utils/clientLabel';
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

// Keyed by the filter unions themselves so a new filter value cannot compile without a word for it,
// and so the chip below can read a label without a search TypeScript must treat as possibly missing.
const CACHE_STATUS_LABEL_KEYS: Record<CacheStatusFilter, string> = {
  all: 'downloads.tab.filters.allItems',
  cached: 'downloads.tab.sessionFilters.cacheStatus.cached',
  missed: 'downloads.tab.sessionFilters.cacheStatus.missed',
  full: 'downloads.tab.sessionFilters.cacheStatus.full',
  evicted: 'common.evicted'
};

const TIME_RANGE_LABEL_KEYS: Record<TimeRangeFilter, string> = {
  all: 'downloads.tab.filters.allItems',
  '1h': 'downloads.tab.sessionFilters.timeRange.lastHour',
  '24h': 'downloads.tab.sessionFilters.timeRange.lastDay',
  '7d': 'downloads.tab.sessionFilters.timeRange.lastWeek'
};

const CACHE_STATUS_OPTIONS: CacheStatusFilter[] = ['all', 'cached', 'missed', 'full', 'evicted'];

const TIME_RANGE_OPTIONS: TimeRangeFilter[] = ['all', '1h', '24h', '7d'];

const SORT_OPTIONS: { value: SessionSortBy; labelKey: string }[] = [
  { value: 'newest', labelKey: 'downloads.tab.sort.recent' },
  { value: 'oldest', labelKey: 'downloads.tab.sort.oldest' },
  { value: 'largest', labelKey: 'downloads.tab.sort.largest' },
  { value: 'smallest', labelKey: 'downloads.tab.sort.smallest' },
  { value: 'bestCache', labelKey: 'downloads.tab.sort.bestCache' },
  { value: 'worstCache', labelKey: 'downloads.tab.sort.worstCache' }
];

// The page-size dropdowns show the number itself, so only the no-limit sentinel needs a word.
const SHOW_ALL_VALUE = '99999';

const SESSIONS_PER_PAGE_OPTIONS: string[] = ['3', '5', '10', SHOW_ALL_VALUE];

const ITEMS_PER_SESSION_OPTIONS: string[] = ['10', '25', '50', SHOW_ALL_VALUE];

const SessionFilterBar: React.FC<SessionFilterBarProps> = ({
  filters,
  updateFilter,
  resetFilters,
  uniqueIps,
  totalCount,
  filteredCount,
  hasActiveFilters
}) => {
  const { t } = useTranslation();
  // Nickname mapping loads for admins and guests; mutations stay AdminOnly server-side.
  const { getGroupForIp } = useClientGroups();
  const { getHostnameForIp } = useClientHostnames();

  // Labels only: every filter value stays the raw IP the downloads are keyed by. The dropdown has to
  // read the same label as the rows it filters, so it shares their precedence rather than its own.
  const labelForIp = (ip: string): string =>
    resolveClientLabel(ip, getGroupForIp(ip)?.nickname, getHostnameForIp(ip)).text;

  const pageSizeLabel = (value: string): string =>
    value === SHOW_ALL_VALUE ? t('downloads.tab.filters.allItems') : value;

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

  const ipOptions = uniqueIps.map((ip: string) => {
    const { text, substitutesAddress } = resolveClientLabel(
      ip,
      getGroupForIp(ip)?.nickname,
      getHostnameForIp(ip)
    );
    return {
      value: ip,
      label: text,
      // The address is worth showing underneath only when the label replaced it.
      description: substitutesAddress ? ip : undefined
    };
  });

  const sortDropdownOptions = SORT_OPTIONS.map((option) => ({
    value: option.value,
    label: t(option.labelKey)
  }));

  const sessionsPerPageDropdownOptions = SESSIONS_PER_PAGE_OPTIONS.map((value: string) => ({
    value,
    label: pageSizeLabel(value)
  }));

  const itemsPerSessionDropdownOptions = ITEMS_PER_SESSION_OPTIONS.map((value: string) => ({
    value,
    label: pageSizeLabel(value)
  }));

  const activeChips: React.ReactNode[] = [];

  // Every active filter reads as a chosen value, so they all carry the shared chip's chosen
  // surface and its x removes exactly that one filter. Each x also names the filter it clears in
  // full, because these labels are sentences and a verb put in front of one does not read as
  // anything a screen reader can act on. Each visible phrase is one key with the value interpolated,
  // since a prefix glued to a translated value puts the words in English order.
  if (filters.clientIps.length > 0) {
    filters.clientIps.forEach((ip: string) => {
      // The address is the user's own data, so it is interpolated verbatim and never looked up.
      const label = labelForIp(ip);
      activeChips.push(
        <IpChip
          key={`ip-${ip}`}
          address={t('downloads.tab.sessionFilters.chips.ip', { value: label })}
          state="added"
          mono={false}
          removeAriaLabel={t('downloads.tab.sessionFilters.chips.removeIp', { value: label })}
          onRemove={() => handleToggleIp(ip)}
        />
      );
    });
  }

  if (filters.cacheStatus !== 'all') {
    activeChips.push(
      <IpChip
        key="cache-status"
        address={t('downloads.tab.sessionFilters.chips.cacheStatus', {
          value: t(CACHE_STATUS_LABEL_KEYS[filters.cacheStatus])
        })}
        state="added"
        mono={false}
        removeAriaLabel={t('downloads.tab.sessionFilters.chips.removeCacheStatus')}
        onRemove={handleClearCacheStatus}
      />
    );
  }

  if (filters.timeRange !== 'all') {
    activeChips.push(
      <IpChip
        key="time-range"
        address={t('downloads.tab.sessionFilters.chips.timeRange', {
          value: t(TIME_RANGE_LABEL_KEYS[filters.timeRange])
        })}
        state="added"
        mono={false}
        removeAriaLabel={t('downloads.tab.sessionFilters.chips.removeTimeRange')}
        onRemove={handleClearTimeRange}
      />
    );
  }

  return (
    <div className="session-filter-bar-wrapper">
      <div className="session-filter-bar well-surface">
        {uniqueIps.length > 1 && (
          <div className="session-filter-group">
            <span className="session-filter-label caps-label">
              {t('downloads.tab.sessionFilters.groups.ip')}
            </span>
            <MultiSelectDropdown
              options={ipOptions}
              values={filters.clientIps}
              onChange={(values: string[]) => updateFilter('clientIps', values)}
              placeholder={t('downloads.tab.sessionFilters.allIps')}
              minSelections={0}
              className="session-filter-ip-select"
              compactMode
            />
          </div>
        )}

        <div className="session-filter-group">
          <span className="session-filter-label caps-label">
            {t('downloads.tab.sessionFilters.groups.cacheStatus')}
          </span>
          <div className="session-filter-pills">
            {CACHE_STATUS_OPTIONS.map((option: CacheStatusFilter) => (
              <TogglePill
                key={option}
                active={filters.cacheStatus === option}
                size="sm"
                onClick={() => updateFilter('cacheStatus', option)}
              >
                {t(CACHE_STATUS_LABEL_KEYS[option])}
              </TogglePill>
            ))}
          </div>
        </div>

        <div className="session-filter-group">
          <span className="session-filter-label caps-label">
            {t('downloads.tab.sessionFilters.groups.timeRange')}
          </span>
          <div className="session-filter-pills">
            {TIME_RANGE_OPTIONS.map((option: TimeRangeFilter) => (
              <TogglePill
                key={option}
                active={filters.timeRange === option}
                size="sm"
                onClick={() => updateFilter('timeRange', option)}
              >
                {t(TIME_RANGE_LABEL_KEYS[option])}
              </TogglePill>
            ))}
          </div>
        </div>

        <div className="session-filter-group">
          <span className="session-filter-label caps-label">
            {t('downloads.tab.sessionFilters.groups.sort')}
          </span>
          <EnhancedDropdown
            options={sortDropdownOptions}
            value={filters.sortBy}
            onChange={(value: string) => updateFilter('sortBy', value as SessionSortBy)}
            compactMode
          />
        </div>

        <div className="session-filter-group">
          <span className="session-filter-label caps-label">
            {t('downloads.tab.sessionFilters.groups.ipsPerPage')}
          </span>
          <EnhancedDropdown
            options={sessionsPerPageDropdownOptions}
            value={String(filters.sessionsPerPage)}
            onChange={(value: string) => updateFilter('sessionsPerPage', Number(value))}
            compactMode
          />
        </div>

        <div className="session-filter-group">
          <span className="session-filter-label caps-label">
            {t('downloads.tab.sessionFilters.groups.itemsPerIp')}
          </span>
          <EnhancedDropdown
            options={itemsPerSessionDropdownOptions}
            value={String(filters.itemsPerSession)}
            onChange={(value: string) => updateFilter('itemsPerSession', Number(value))}
            compactMode
          />
        </div>

        {hasActiveFilters && (
          <span className="session-filter-count">
            {t('downloads.tab.sessionFilters.showingCount', {
              shown: filteredCount,
              total: totalCount
            })}
          </span>
        )}

        {hasActiveFilters && (
          <Button
            type="button"
            variant="transparent"
            size="xs"
            className="session-filter-clear"
            onClick={resetFilters}
          >
            {t('common.clear')}
          </Button>
        )}
      </div>

      {activeChips.length > 0 && <div className="flex flex-wrap gap-2 mt-2">{activeChips}</div>}
    </div>
  );
};

export default SessionFilterBar;
