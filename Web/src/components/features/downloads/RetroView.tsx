import React from 'react';
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from 'lucide-react';
import { formatBytes, formatPercent, formatDateTime } from '@utils/formatters';
import { SteamIcon } from '@components/ui/SteamIcon';
import { WsusIcon } from '@components/ui/WsusIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { EAIcon } from '@components/ui/EAIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { UnknownServiceIcon } from '@components/ui/UnknownServiceIcon';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Card } from '@components/ui/Card';
import type { Download, DownloadGroup } from '../../../types';

const API_BASE = '/api';

// Default items per page for retro view (performance optimization)
const DEFAULT_ITEMS_PER_PAGE = 100;

interface RetroViewProps {
  items: (Download | DownloadGroup)[];
}

// Represents a grouped download by depot
interface DepotGroup {
  depotId: number | null;
  service: string;
  gameName: string | null;
  gameAppId: number | null;
  clientIp: string;
  downloads: Download[];
  startTimeUtc: string;
  endTimeUtc: string;
  cacheHitBytes: number;
  cacheMissBytes: number;
  totalBytes: number;
}

// Flatten groups into individual downloads, then group by depot
const flattenAndGroupByDepot = (items: (Download | DownloadGroup)[]): DepotGroup[] => {
  const downloads: Download[] = [];

  for (const item of items) {
    if ('downloads' in item) {
      // It's a group - add all its downloads
      downloads.push(...item.downloads);
    } else {
      // It's an individual download
      downloads.push(item);
    }
  }

  // Sort by start time, most recent first
  downloads.sort(
    (a, b) => new Date(b.startTimeUtc).getTime() - new Date(a.startTimeUtc).getTime()
  );

  // Group by depot ID (or create individual entries for non-depot downloads)
  const depotGroups = new Map<string, DepotGroup>();

  for (const download of downloads) {
    // Create a unique key based on depot, game, and client
    const key = download.depotId
      ? `depot-${download.depotId}-${download.clientIp}`
      : `individual-${download.id}`;

    if (depotGroups.has(key)) {
      const group = depotGroups.get(key)!;
      group.downloads.push(download);
      group.cacheHitBytes += download.cacheHitBytes || 0;
      group.cacheMissBytes += download.cacheMissBytes || 0;
      group.totalBytes += download.totalBytes || 0;
      // Update time range
      if (new Date(download.startTimeUtc) < new Date(group.startTimeUtc)) {
        group.startTimeUtc = download.startTimeUtc;
      }
      if (download.endTimeUtc && new Date(download.endTimeUtc) > new Date(group.endTimeUtc)) {
        group.endTimeUtc = download.endTimeUtc;
      }
    } else {
      depotGroups.set(key, {
        depotId: download.depotId || null,
        service: download.service,
        gameName: download.gameName || null,
        gameAppId: download.gameAppId || null,
        clientIp: download.clientIp,
        downloads: [download],
        startTimeUtc: download.startTimeUtc,
        endTimeUtc: download.endTimeUtc || download.startTimeUtc,
        cacheHitBytes: download.cacheHitBytes || 0,
        cacheMissBytes: download.cacheMissBytes || 0,
        totalBytes: download.totalBytes || 0
      });
    }
  }

  // Convert to array and sort by most recent end time
  return Array.from(depotGroups.values()).sort(
    (a, b) => new Date(b.endTimeUtc).getTime() - new Date(a.endTimeUtc).getTime()
  );
};

const getServiceIcon = (service: string, size: number = 24) => {
  const serviceLower = service.toLowerCase();
  const style = { opacity: 0.8 };

  switch (serviceLower) {
    case 'steam':
      return <SteamIcon size={size} style={{ ...style, color: 'var(--theme-steam)' }} />;
    case 'wsus':
    case 'windows':
      return <WsusIcon size={size} style={{ ...style, color: 'var(--theme-wsus)' }} />;
    case 'riot':
    case 'riotgames':
      return <RiotIcon size={size} style={{ ...style, color: 'var(--theme-riot)' }} />;
    case 'epic':
    case 'epicgames':
      return <EpicIcon size={size} style={style} />;
    case 'origin':
    case 'ea':
      return <EAIcon size={size} style={{ ...style, color: 'var(--theme-origin)' }} />;
    case 'blizzard':
    case 'battle.net':
    case 'battlenet':
      return <BlizzardIcon size={size} style={{ ...style, color: 'var(--theme-blizzard)' }} />;
    case 'xbox':
    case 'xboxlive':
      return <XboxIcon size={size} style={{ ...style, color: 'var(--theme-xbox)' }} />;
    default:
      return <UnknownServiceIcon size={size} style={{ ...style, color: 'var(--theme-text-secondary)' }} />;
  }
};

const RetroView: React.FC<RetroViewProps> = ({ items }) => {
  const [imageErrors, setImageErrors] = React.useState<Set<string>>(new Set());
  const [currentPage, setCurrentPage] = React.useState(1);
  const [itemsPerPage, setItemsPerPage] = React.useState<number | 'all'>(DEFAULT_ITEMS_PER_PAGE);

  const allDepotGroups = React.useMemo(() => flattenAndGroupByDepot(items), [items]);

  // Reset to page 1 when items change
  React.useEffect(() => {
    setCurrentPage(1);
  }, [items]);

  const handleImageError = (gameAppId: string) => {
    setImageErrors((prev) => new Set(prev).add(gameAppId));
  };

  // Pagination calculations
  const totalItems = allDepotGroups.length;
  const effectiveItemsPerPage = itemsPerPage === 'all' ? totalItems : itemsPerPage;
  const totalPages = Math.ceil(totalItems / effectiveItemsPerPage);
  const startIndex = (currentPage - 1) * effectiveItemsPerPage;
  const endIndex = Math.min(startIndex + effectiveItemsPerPage, totalItems);
  const depotGroups = allDepotGroups.slice(startIndex, endIndex);

  const handlePageChange = (newPage: number) => {
    if (newPage >= 1 && newPage <= totalPages) {
      setCurrentPage(newPage);
    }
  };

  const itemsPerPageOptions = [
    { value: '50', label: '50' },
    { value: '100', label: '100' },
    { value: '200', label: '200' },
    { value: 'all', label: 'All' }
  ];

  return (
    <div className="space-y-4">
      {/* Pagination Controls - Top */}
      <Card padding="sm">
        <div className="flex flex-col sm:flex-row items-center justify-between gap-3">
          {/* Items per page selector */}
          <div className="flex items-center gap-2">
            <span className="text-sm text-[var(--theme-text-secondary)]">Show:</span>
            <EnhancedDropdown
              options={itemsPerPageOptions}
              value={itemsPerPage === 'all' ? 'all' : itemsPerPage.toString()}
              onChange={(value) => {
                setItemsPerPage(value === 'all' ? 'all' : parseInt(value));
                setCurrentPage(1);
              }}
              className="w-24"
            />
            <span className="text-sm text-[var(--theme-text-muted)]">
              {totalItems} total downloads
            </span>
          </div>

          {/* Page info and navigation */}
          {itemsPerPage !== 'all' && totalPages > 1 && (
            <div className="flex items-center gap-2">
              <span className="text-sm text-[var(--theme-text-secondary)]">
                {startIndex + 1}-{endIndex} of {totalItems}
              </span>
              <div className="flex items-center gap-1">
                <button
                  onClick={() => handlePageChange(1)}
                  disabled={currentPage === 1}
                  className="p-1.5 rounded transition-colors disabled:opacity-40 disabled:cursor-not-allowed hover:bg-[var(--theme-bg-tertiary)]"
                  title="First page"
                >
                  <ChevronsLeft size={16} />
                </button>
                <button
                  onClick={() => handlePageChange(currentPage - 1)}
                  disabled={currentPage === 1}
                  className="p-1.5 rounded transition-colors disabled:opacity-40 disabled:cursor-not-allowed hover:bg-[var(--theme-bg-tertiary)]"
                  title="Previous page"
                >
                  <ChevronLeft size={16} />
                </button>
                <span className="text-sm font-mono px-2 min-w-[60px] text-center text-[var(--theme-text-primary)]">
                  {currentPage} / {totalPages}
                </span>
                <button
                  onClick={() => handlePageChange(currentPage + 1)}
                  disabled={currentPage === totalPages}
                  className="p-1.5 rounded transition-colors disabled:opacity-40 disabled:cursor-not-allowed hover:bg-[var(--theme-bg-tertiary)]"
                  title="Next page"
                >
                  <ChevronRight size={16} />
                </button>
                <button
                  onClick={() => handlePageChange(totalPages)}
                  disabled={currentPage === totalPages}
                  className="p-1.5 rounded transition-colors disabled:opacity-40 disabled:cursor-not-allowed hover:bg-[var(--theme-bg-tertiary)]"
                  title="Last page"
                >
                  <ChevronsRight size={16} />
                </button>
              </div>
            </div>
          )}
        </div>
      </Card>

      {/* Table */}
      <div className="rounded-lg border overflow-hidden" style={{ borderColor: 'var(--theme-border-primary)' }}>
        {/* Desktop Table Header - hidden on mobile */}
        <div
          className="hidden lg:grid grid-cols-[minmax(180px,1fr)_minmax(200px,2fr)_100px_120px_minmax(130px,1fr)_minmax(130px,1fr)_100px] gap-2 px-4 py-3 text-xs font-semibold uppercase tracking-wide border-b"
          style={{
            backgroundColor: 'var(--theme-bg-tertiary)',
            borderColor: 'var(--theme-border-secondary)',
            color: 'var(--theme-text-secondary)'
          }}
        >
          <div>Timestamp</div>
          <div>App</div>
          <div>Depot</div>
          <div>Client</div>
          <div>Cache Hit</div>
          <div>Cache Miss</div>
          <div className="text-center">Overall</div>
        </div>

        {/* Table Body */}
        <div>
          {depotGroups.map((group, index) => {
            const serviceLower = group.service.toLowerCase();
            const isSteam = serviceLower === 'steam';
            const hasGameImage = isSteam &&
              group.gameAppId &&
              group.gameName &&
              group.gameName !== 'Unknown Steam Game' &&
              !group.gameName.match(/^Steam App \d+$/) &&
              !imageErrors.has(String(group.gameAppId));

            const totalBytes = group.totalBytes || 0;
            const cacheHitBytes = group.cacheHitBytes || 0;
            const cacheMissBytes = group.cacheMissBytes || 0;
            const hitPercent = totalBytes > 0 ? (cacheHitBytes / totalBytes) * 100 : 0;
            const missPercent = totalBytes > 0 ? (cacheMissBytes / totalBytes) * 100 : 0;

            // Format timestamp range
            const startTime = formatDateTime(group.startTimeUtc);
            const endTime = formatDateTime(group.endTimeUtc);
            const timeRange = startTime === endTime ? startTime : `${startTime} - ${endTime}`;

            // Unique key for each group
            const groupKey = group.depotId
              ? `depot-${group.depotId}-${group.clientIp}`
              : `individual-${group.downloads[0]?.id || index}`;

            // Show session count if more than 1
            const sessionCount = group.downloads.length;

            return (
              <div
                key={groupKey}
                className="transition-colors hover:bg-[var(--theme-bg-tertiary)]/30"
                style={{
                  backgroundColor: index % 2 === 0 ? 'var(--theme-bg-secondary)' : 'var(--theme-bg-primary)',
                  borderBottom: index < depotGroups.length - 1 ? '1px solid var(--theme-border-secondary)' : 'none'
                }}
              >
                {/* Mobile Layout */}
                <div className="lg:hidden p-3 space-y-3">
                  {/* App image and name */}
                  <div className="flex items-center gap-3">
                    {hasGameImage && group.gameAppId ? (
                      <img
                        src={`${API_BASE}/game-images/${group.gameAppId}/header/`}
                        alt={group.gameName || 'Game'}
                        className="w-[100px] h-[38px] rounded object-cover flex-shrink-0"
                        loading="lazy"
                        onError={() => handleImageError(String(group.gameAppId))}
                      />
                    ) : (
                      <div
                        className="w-[100px] h-[38px] rounded flex items-center justify-center flex-shrink-0"
                        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                      >
                        {getServiceIcon(group.service, 24)}
                      </div>
                    )}
                    <div className="flex-1 min-w-0">
                      <div className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                        {group.gameName || group.service}
                        {sessionCount > 1 && (
                          <span className="ml-2 text-xs text-[var(--theme-text-muted)]">
                            ({sessionCount} sessions)
                          </span>
                        )}
                      </div>
                      <div className="text-xs text-[var(--theme-text-muted)]">
                        {group.clientIp}
                        {group.depotId && (
                          <>
                            {' • '}
                            <a
                              href={`https://steamdb.info/depot/${group.depotId}/`}
                              target="_blank"
                              rel="noopener noreferrer"
                              className="text-[var(--theme-primary)] hover:underline"
                            >
                              {group.depotId}
                            </a>
                          </>
                        )}
                      </div>
                    </div>
                  </div>

                  {/* Timestamp */}
                  <div className="text-xs text-[var(--theme-text-secondary)]">
                    {timeRange}
                  </div>

                  {/* Cache bars and Overall */}
                  <div className="grid grid-cols-3 gap-3">
                    {/* Cache Hit */}
                    <div className="flex flex-col gap-1">
                      <div className="text-xs text-[var(--theme-text-muted)]">Hit</div>
                      <div
                        className="h-3 rounded-full overflow-hidden"
                        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                      >
                        <div
                          className="h-full rounded-full transition-all"
                          style={{
                            width: `${hitPercent}%`,
                            backgroundColor: hitPercent > 0 ? 'var(--theme-success)' : 'transparent'
                          }}
                        />
                      </div>
                      <div className="text-xs text-[var(--theme-text-secondary)]">
                        {formatBytes(cacheHitBytes)}
                      </div>
                    </div>

                    {/* Cache Miss */}
                    <div className="flex flex-col gap-1">
                      <div className="text-xs text-[var(--theme-text-muted)]">Miss</div>
                      <div
                        className="h-3 rounded-full overflow-hidden"
                        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                      >
                        <div
                          className="h-full rounded-full transition-all"
                          style={{
                            width: `${missPercent}%`,
                            backgroundColor: missPercent > 0 ? 'var(--theme-error)' : 'transparent'
                          }}
                        />
                      </div>
                      <div className="text-xs text-[var(--theme-text-secondary)]">
                        {formatBytes(cacheMissBytes)}
                      </div>
                    </div>

                    {/* Overall */}
                    <div className="flex flex-col items-center justify-center gap-0.5">
                      <div className="text-xs text-[var(--theme-text-muted)]">Overall</div>
                      <div
                        className="text-base font-bold"
                        style={{
                          color: hitPercent >= 90
                            ? 'var(--theme-success)'
                            : hitPercent >= 50
                              ? 'var(--theme-warning)'
                              : 'var(--theme-error)'
                        }}
                      >
                        {formatPercent(hitPercent)}
                      </div>
                      <div
                        className="text-[9px] font-medium uppercase"
                        style={{
                          color: hitPercent >= 90
                            ? 'var(--theme-success-text)'
                            : hitPercent >= 50
                              ? 'var(--theme-warning-text)'
                              : 'var(--theme-error-text)'
                        }}
                      >
                        {hitPercent >= 90 ? 'Excellent' : hitPercent >= 50 ? 'Partial' : 'Miss'}
                      </div>
                    </div>
                  </div>
                </div>

                {/* Desktop Layout */}
                <div className="hidden lg:grid grid-cols-[minmax(180px,1fr)_minmax(200px,2fr)_100px_120px_minmax(130px,1fr)_minmax(130px,1fr)_100px] gap-2 px-4 py-3 items-center">
                  {/* Timestamp */}
                  <div className="text-xs text-[var(--theme-text-secondary)]">
                    {timeRange}
                  </div>

                  {/* App - with game image */}
                  <div className="flex items-center gap-3">
                    {hasGameImage && group.gameAppId ? (
                      <img
                        src={`${API_BASE}/game-images/${group.gameAppId}/header/`}
                        alt={group.gameName || 'Game'}
                        className="w-[120px] h-[45px] rounded object-cover flex-shrink-0"
                        loading="lazy"
                        onError={() => handleImageError(String(group.gameAppId))}
                      />
                    ) : (
                      <div
                        className="w-[120px] h-[45px] rounded flex items-center justify-center flex-shrink-0"
                        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                      >
                        {getServiceIcon(group.service, 28)}
                      </div>
                    )}
                    <div className="flex flex-col min-w-0">
                      <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                        {group.gameName || group.service}
                      </span>
                      {sessionCount > 1 && (
                        <span className="text-xs text-[var(--theme-text-muted)]">
                          {sessionCount} sessions
                        </span>
                      )}
                    </div>
                  </div>

                  {/* Depot */}
                  <div>
                    {group.depotId ? (
                      <a
                        href={`https://steamdb.info/depot/${group.depotId}/`}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="text-sm font-mono text-[var(--theme-primary)] hover:underline"
                      >
                        {group.depotId}
                      </a>
                    ) : (
                      <span className="text-sm text-[var(--theme-text-muted)]">N/A</span>
                    )}
                  </div>

                  {/* Client IP */}
                  <div className="text-sm font-mono text-[var(--theme-text-primary)]">
                    {group.clientIp}
                  </div>

                  {/* Cache Hit - Progress bar style */}
                  <div className="flex flex-col gap-1">
                    <div className="flex items-center gap-2">
                      <div
                        className="flex-1 h-4 rounded-full overflow-hidden"
                        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                      >
                        <div
                          className="h-full rounded-full transition-all"
                          style={{
                            width: `${hitPercent}%`,
                            backgroundColor: hitPercent > 0 ? 'var(--theme-success)' : 'transparent'
                          }}
                        />
                      </div>
                    </div>
                    <div className="text-xs text-[var(--theme-text-secondary)]">
                      {formatBytes(cacheHitBytes)} • {formatPercent(hitPercent)}
                    </div>
                  </div>

                  {/* Cache Miss - Progress bar style */}
                  <div className="flex flex-col gap-1">
                    <div className="flex items-center gap-2">
                      <div
                        className="flex-1 h-4 rounded-full overflow-hidden"
                        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                      >
                        <div
                          className="h-full rounded-full transition-all"
                          style={{
                            width: `${missPercent}%`,
                            backgroundColor: missPercent > 0 ? 'var(--theme-error)' : 'transparent'
                          }}
                        />
                      </div>
                    </div>
                    <div className="text-xs text-[var(--theme-text-secondary)]">
                      {formatBytes(cacheMissBytes)} • {formatPercent(missPercent)}
                    </div>
                  </div>

                  {/* Overall - Visual indicator of cache efficiency */}
                  <div className="flex flex-col items-center justify-center gap-1">
                    <div
                      className="text-lg font-bold"
                      style={{
                        color: hitPercent >= 90
                          ? 'var(--theme-success)'
                          : hitPercent >= 50
                            ? 'var(--theme-warning)'
                            : 'var(--theme-error)'
                      }}
                    >
                      {formatPercent(hitPercent)}
                    </div>
                    <div
                      className="text-[10px] font-medium uppercase tracking-wide"
                      style={{
                        color: hitPercent >= 90
                          ? 'var(--theme-success-text)'
                          : hitPercent >= 50
                            ? 'var(--theme-warning-text)'
                            : 'var(--theme-error-text)'
                      }}
                    >
                      {hitPercent >= 90 ? 'Excellent' : hitPercent >= 50 ? 'Partial' : 'Miss'}
                    </div>
                  </div>
                </div>
              </div>
            );
          })}
        </div>

        {depotGroups.length === 0 && (
          <div className="px-4 py-8 text-center text-[var(--theme-text-muted)]">
            No downloads to display
          </div>
        )}
      </div>

      {/* Pagination Controls - Bottom (for longer lists) */}
      {itemsPerPage !== 'all' && totalPages > 1 && (
        <Card padding="sm">
          <div className="flex items-center justify-center gap-2">
            <button
              onClick={() => handlePageChange(1)}
              disabled={currentPage === 1}
              className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
              style={{
                backgroundColor: 'var(--theme-bg-secondary)',
                color: 'var(--theme-text-primary)',
                border: '1px solid var(--theme-border-primary)'
              }}
              title="First page"
            >
              <ChevronsLeft size={16} />
            </button>
            <button
              onClick={() => handlePageChange(currentPage - 1)}
              disabled={currentPage === 1}
              className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
              style={{
                backgroundColor: 'var(--theme-bg-secondary)',
                color: 'var(--theme-text-primary)',
                border: '1px solid var(--theme-border-primary)'
              }}
              title="Previous page"
            >
              <ChevronLeft size={16} />
            </button>

            {/* Page Numbers */}
            <div className="flex items-center gap-1 px-2">
              {totalPages <= 7 ? (
                Array.from({ length: totalPages }, (_, i) => i + 1).map((pageNum) => (
                  <button
                    key={pageNum}
                    onClick={() => handlePageChange(pageNum)}
                    className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
                      currentPage === pageNum ? 'shadow-md' : 'hover:bg-opacity-80'
                    }`}
                    style={{
                      backgroundColor:
                        currentPage === pageNum
                          ? 'var(--theme-primary)'
                          : 'var(--theme-bg-tertiary)',
                      color:
                        currentPage === pageNum
                          ? 'var(--theme-button-text)'
                          : 'var(--theme-text-primary)',
                      border:
                        currentPage === pageNum
                          ? '1px solid var(--theme-primary)'
                          : '1px solid var(--theme-border-secondary)'
                    }}
                  >
                    {pageNum}
                  </button>
                ))
              ) : (
                <>
                  <button
                    onClick={() => handlePageChange(1)}
                    className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
                      currentPage === 1 ? 'shadow-md' : 'hover:bg-opacity-80'
                    }`}
                    style={{
                      backgroundColor:
                        currentPage === 1 ? 'var(--theme-primary)' : 'var(--theme-bg-tertiary)',
                      color:
                        currentPage === 1
                          ? 'var(--theme-button-text)'
                          : 'var(--theme-text-primary)',
                      border:
                        currentPage === 1
                          ? '1px solid var(--theme-primary)'
                          : '1px solid var(--theme-border-secondary)'
                    }}
                  >
                    1
                  </button>

                  {currentPage > 3 && (
                    <span className="px-2" style={{ color: 'var(--theme-text-muted)' }}>
                      •••
                    </span>
                  )}

                  {Array.from({ length: 5 }, (_, i) => {
                    const pageNum = currentPage - 2 + i;
                    if (pageNum <= 1 || pageNum >= totalPages) return null;
                    return (
                      <button
                        key={pageNum}
                        onClick={() => handlePageChange(pageNum)}
                        className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
                          currentPage === pageNum ? 'shadow-md' : 'hover:bg-opacity-80'
                        }`}
                        style={{
                          backgroundColor:
                            currentPage === pageNum
                              ? 'var(--theme-primary)'
                              : 'var(--theme-bg-tertiary)',
                          color:
                            currentPage === pageNum
                              ? 'var(--theme-button-text)'
                              : 'var(--theme-text-primary)',
                          border:
                            currentPage === pageNum
                              ? '1px solid var(--theme-primary)'
                              : '1px solid var(--theme-border-secondary)'
                        }}
                      >
                        {pageNum}
                      </button>
                    );
                  }).filter(Boolean)}

                  {currentPage < totalPages - 2 && (
                    <span className="px-2" style={{ color: 'var(--theme-text-muted)' }}>
                      •••
                    </span>
                  )}

                  <button
                    onClick={() => handlePageChange(totalPages)}
                    className={`min-w-[32px] h-8 px-2 rounded-lg font-medium transition-all hover:scale-105 ${
                      currentPage === totalPages ? 'shadow-md' : 'hover:bg-opacity-80'
                    }`}
                    style={{
                      backgroundColor:
                        currentPage === totalPages
                          ? 'var(--theme-primary)'
                          : 'var(--theme-bg-tertiary)',
                      color:
                        currentPage === totalPages
                          ? 'var(--theme-button-text)'
                          : 'var(--theme-text-primary)',
                      border:
                        currentPage === totalPages
                          ? '1px solid var(--theme-primary)'
                          : '1px solid var(--theme-border-secondary)'
                    }}
                  >
                    {totalPages}
                  </button>
                </>
              )}
            </div>

            <button
              onClick={() => handlePageChange(currentPage + 1)}
              disabled={currentPage === totalPages}
              className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
              style={{
                backgroundColor: 'var(--theme-bg-secondary)',
                color: 'var(--theme-text-primary)',
                border: '1px solid var(--theme-border-primary)'
              }}
              title="Next page"
            >
              <ChevronRight size={16} />
            </button>
            <button
              onClick={() => handlePageChange(totalPages)}
              disabled={currentPage === totalPages}
              className="p-2 rounded-lg transition-all hover:scale-105 disabled:opacity-40 disabled:cursor-not-allowed disabled:hover:scale-100"
              style={{
                backgroundColor: 'var(--theme-bg-secondary)',
                color: 'var(--theme-text-primary)',
                border: '1px solid var(--theme-border-primary)'
              }}
              title="Last page"
            >
              <ChevronsRight size={16} />
            </button>
          </div>
        </Card>
      )}
    </div>
  );
};

export default RetroView;
