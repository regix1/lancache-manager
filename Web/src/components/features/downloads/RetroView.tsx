import React, {
  useState,
  useRef,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useImperativeHandle,
  forwardRef,
  memo
} from 'react';
import { useTranslation } from 'react-i18next';
import { HardDrive, Download } from 'lucide-react';
import { useVirtualizer } from '@tanstack/react-virtual';

import './VirtualizedList.css';
import type { RetroViewHandle, RetroRowData, HitMissFilter } from './RetroView.types';
import { useIsDesktop } from '@hooks/useMediaQuery';
import { useAvailableGameImages } from '@hooks/useAvailableGameImages';
import { formatBytes, formatPercent, formatSpeed } from '@utils/formatters';
import type { ColumnWidths } from '@utils/textMeasurement';
import { Alert } from '@components/ui/Alert';
import { Pagination } from '@components/ui/Pagination';
import { useDownloadAssociations } from '@contexts/useDownloadAssociations';
import { resolveGameDetection } from '@utils/gameDetection';
import { nameKeyedImageKey } from '@utils/gameBannerSlug';
import RetroRow from './RetroRow';
import { useRetroDownloads } from './useRetroDownloads';
import {
  formatTimeRange,
  formatTimeRangeLines,
  groupByDepot,
  mapDtoToDepotGroupedData,
  type DepotGroupedData,
  type RetroSortOrder
} from './retroGrouping';
import {
  RETRO_WIDTHS_STORAGE_KEY,
  RESIZE_MIN_WIDTH,
  buildGridTemplate,
  fitWidthsToContainer,
  getDefaultColumnWidths,
  measureAllRetroColumns,
  measureRetroColumn,
  type RetroColumnVisibility,
  type RetroMeasureRow
} from './retroColumnSizing';
import type {
  Download as DownloadType,
  DownloadGroup,
  EventSummary,
  GameDetectionSummary
} from '../../../types';

interface RetroViewProps {
  items: (DownloadType | DownloadGroup)[];
  sortOrder: string;
  itemsPerPage: number;
  currentPage: number;
  onPageChange: (page: number) => void;
  showTimestamps: boolean;
  showBannerColumn: boolean;
  aestheticMode?: boolean;
  showDatasourceLabels?: boolean;
  hasMultipleDatasources?: boolean;
  groupByGame?: boolean;
  /** Server-side merge: collapses every row for the same service into one row, overriding groupByGame. Only used when serverMode is true. */
  groupByService?: boolean;
  detectionLookup?: Map<number, GameDetectionSummary> | null;
  detectionByName?: Map<string, GameDetectionSummary> | null;
  detectionByService?: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
  /**
   * When true, RetroView fetches its own server-paginated data from
   * `/api/downloads/retro` instead of consuming the `items` prop.
   */
  serverMode?: boolean;
  /** Server-side filter: service name or 'all'. Only used when serverMode is true. */
  filterService?: string;
  /** Server-side filter: client IP or 'all'. Only used when serverMode is true. */
  filterClient?: string;
  /** Server-side filter: free-text search. Only used when serverMode is true. */
  filterSearch?: string;
  /** Server-side filter: hide localhost rows. Only used when serverMode is true. */
  filterHideLocalhost?: boolean;
  /** Server-side filter: hide zero-byte rows. Only used when serverMode is true. */
  filterHideMetadata?: boolean;
  /** Server-side filter: hide rows whose game name is unknown. Only used when serverMode is true. */
  filterHideUnknown?: boolean;
  /** Server-side filter: hit/miss bucket ('all' | 'hit' | 'miss'). Only used when serverMode is true. */
  filterHitMiss?: HitMissFilter;
  /** Server-side filter: Unix start time (seconds). Only used when serverMode is true. */
  filterStartTime?: number;
  /** Server-side filter: Unix end time (seconds). Only used when serverMode is true. */
  filterEndTime?: number;
  /** Server-side filter: event ID. Only used when serverMode is true. */
  filterEventId?: number;
}

// Empty State Component
const EmptyState: React.FC = () => {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col items-center justify-center py-16 px-4">
      <div className="relative mb-6 retro-empty-float">
        {/* Animated icon container */}
        <div className="w-20 h-20 rounded-2xl flex items-center justify-center bg-gradient-to-br from-[var(--theme-bg-tertiary)] to-[var(--theme-bg-secondary)] shadow-[0_8px_32px_rgba(0,0,0,0.2)]">
          <HardDrive size={36} className="text-[var(--theme-text-muted)] opacity-60" />
        </div>
        {/* Decorative elements */}
        <div className="absolute -top-2 -right-2 w-6 h-6 rounded-full flex items-center justify-center bg-[var(--theme-bg-tertiary)] border-2 border-[var(--theme-border-secondary)]">
          <Download size={12} className="text-[var(--theme-text-muted)]" />
        </div>
      </div>
      <h3 className="text-lg font-semibold mb-2 text-[var(--theme-text-primary)]">
        {t('downloads.tab.retro.empty.title')}
      </h3>
      <p className="text-sm text-center max-w-xs text-[var(--theme-text-muted)]">
        {t('downloads.tab.retro.empty.description')}
      </p>
      {/* Decorative dots */}
      <div className="flex gap-1.5 mt-6">
        {[0, 1, 2].map((i) => (
          <div key={i} className="retro-empty-dot" />
        ))}
      </div>
    </div>
  );
};

// First-load placeholder: shimmer rows under the real header so the table
// frame is stable while the server page is fetched (no header-only flash,
// no external spinner pushing the layout around).
const SKELETON_ROW_COUNT = 8;
const RetroSkeletonRows: React.FC<{ isDesktop: boolean; visibility: RetroColumnVisibility }> = ({
  isDesktop,
  visibility
}) => (
  <div className="retro-skeleton" aria-hidden="true">
    {Array.from({ length: SKELETON_ROW_COUNT }, (_, i) =>
      isDesktop ? (
        <div key={i} className="retro-grid-row retro-body-row items-center">
          {visibility.showTimestamps && (
            <div className="px-2">
              <div className="retro-skeleton-bar w-4/5" />
              <div className="retro-skeleton-bar retro-skeleton-bar-sub w-3/5" />
            </div>
          )}
          {visibility.showBanner && (
            <div className="px-2 flex justify-center">
              <div className="retro-skeleton-banner" />
            </div>
          )}
          <div className="px-2">
            <div className="retro-skeleton-bar w-3/4" />
            <div className="retro-skeleton-bar retro-skeleton-bar-sub w-1/3" />
          </div>
          {visibility.showDatasource && (
            <div className="px-2 flex justify-center">
              <div className="retro-skeleton-bar w-2/3" />
            </div>
          )}
          <div className="px-2 flex justify-center">
            <div className="retro-skeleton-bar w-1/2" />
          </div>
          <div className="px-2 flex justify-end">
            <div className="retro-skeleton-bar w-2/3" />
          </div>
          <div className="px-2 flex justify-end">
            <div className="retro-skeleton-bar w-3/4" />
          </div>
          <div className="px-2 flex justify-end">
            <div className="retro-skeleton-bar w-1/2" />
          </div>
          <div className="px-2">
            <div className="retro-skeleton-bar w-full" />
          </div>
          <div className="px-2 flex justify-center">
            <div className="retro-skeleton-gauge" />
          </div>
        </div>
      ) : (
        <div key={i} className="retro-body-row">
          <div className="flex items-center gap-3">
            <div className="retro-skeleton-banner" />
            <div className="flex-1 min-w-0">
              <div className="retro-skeleton-bar w-2/3" />
              <div className="retro-skeleton-bar retro-skeleton-bar-sub w-1/3" />
            </div>
          </div>
          <div className="retro-skeleton-bar w-full mt-3" />
        </div>
      )
    )}
  </div>
);

// Cheap equality guard so the auto-fit layout effect can skip no-op state
// updates instead of re-rendering every row after every fetch.
const widthsEqual = (a: ColumnWidths, b: ColumnWidths): boolean =>
  (Object.keys(a) as (keyof ColumnWidths)[]).every((key) => a[key] === b[key]);

// Column resize handle component
const ResizeHandle: React.FC<{
  onMouseDown: (e: React.MouseEvent) => void;
  onDoubleClick: (e: React.MouseEvent) => void;
}> = ({ onMouseDown, onDoubleClick }) => (
  <div
    className="absolute right-0 top-0 bottom-0 w-1.5 cursor-col-resize group z-10 flex items-center justify-end"
    onMouseDown={onMouseDown}
    onDoubleClick={onDoubleClick}
  >
    {/* Subtle divider - always visible */}
    <div className="h-4 w-px rounded transition-[width,height] duration-150 group-hover:h-full group-hover:w-0.5 bg-[var(--theme-primary)] opacity-30" />
    {/* Brighter line on hover */}
    <div className="absolute h-full w-0.5 rounded opacity-0 group-hover:opacity-100 transition-opacity bg-[var(--theme-primary)]" />
  </div>
);

// Virtualization: RetroView rows have heavy content (banners, columns,
// tooltips). Threshold is lower (>100) because row cost is high.
const RETRO_VIRTUALIZATION_THRESHOLD = 100;

const RetroView = memo(
  forwardRef<RetroViewHandle, RetroViewProps>(
    (
      {
        items,
        sortOrder,
        itemsPerPage,
        currentPage,
        onPageChange,
        showTimestamps,
        showBannerColumn,
        aestheticMode = false,
        showDatasourceLabels = true,
        hasMultipleDatasources = false,
        groupByGame = false,
        groupByService = false,
        detectionLookup = null,
        detectionByName = null,
        detectionByService = null,
        serverMode = false,
        filterService = 'all',
        filterClient = 'all',
        filterSearch = '',
        filterHideLocalhost = false,
        filterHideMetadata = false,
        filterHideUnknown = false,
        filterHitMiss = 'all',
        filterStartTime,
        filterEndTime,
        filterEventId
      },
      ref
    ) => {
      const { t } = useTranslation();
      const [imageErrors, setImageErrors] = useState<Set<string>>(new Set());
      const availableImages = useAvailableGameImages();

      // Use JavaScript-based breakpoint detection for conditional rendering
      // This completely removes desktop layout from DOM on mobile, preventing width calculation conflicts
      const isDesktop = useIsDesktop();

      // Event associations for download badges
      const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();

      // Server-paginated fetch via /api/downloads/retro. The hook is always
      // declared (hooks cannot be conditional), but it only fetches when
      // `serverMode` is true.
      const serverRetro = useRetroDownloads({
        enabled: serverMode,
        page: currentPage,
        pageSize: itemsPerPage,
        sort: sortOrder,
        service: filterService,
        client: filterClient,
        search: filterSearch,
        hideLocalhost: filterHideLocalhost,
        hideMetadata: filterHideMetadata,
        hideUnknown: filterHideUnknown,
        hitMiss: filterHitMiss,
        groupByGame,
        groupByService,
        startTime: filterStartTime,
        endTime: filterEndTime,
        eventId: filterEventId
      });

      // Client-side grouping path: only runs in non-server mode.
      const clientGroupedItems = useMemo(() => {
        if (serverMode) return [] as DepotGroupedData[];
        return groupByDepot(items, sortOrder as RetroSortOrder, groupByGame);
      }, [serverMode, items, sortOrder, groupByGame]);

      // Server-mode page rows: one DepotGroupedData per server DTO.
      // The server already merges by game when groupByGame is true, so we
      // return the mapped rows directly - no client-side mergeByGame needed.
      const serverGroupedItems = useMemo(() => {
        if (!serverMode) return [] as DepotGroupedData[];
        return serverRetro.items.map(mapDtoToDepotGroupedData);
      }, [serverMode, serverRetro.items]);

      // Calculate total pages - server response wins when available.
      const totalPages = useMemo(() => {
        if (serverMode) {
          return Math.max(1, serverRetro.totalPages);
        }
        return Math.max(1, Math.ceil(clientGroupedItems.length / itemsPerPage));
      }, [serverMode, serverRetro.totalPages, clientGroupedItems.length, itemsPerPage]);

      // Page slice. In server mode the rows ARE the page; no slicing.
      const groupedItems = useMemo(() => {
        if (serverMode) {
          return serverGroupedItems;
        }
        const start = (currentPage - 1) * itemsPerPage;
        return clientGroupedItems.slice(start, start + itemsPerPage);
      }, [serverMode, serverGroupedItems, clientGroupedItems, currentPage, itemsPerPage]);

      // Total items for pagination footer label.
      const totalItems = serverMode ? serverRetro.totalItems : clientGroupedItems.length;

      // Only show datasource column when there are multiple datasources
      const showDatasourceColumn = hasMultipleDatasources && showDatasourceLabels;
      const visibility = useMemo<RetroColumnVisibility>(
        () => ({
          showDatasource: showDatasourceColumn,
          showTimestamps,
          showBanner: showBannerColumn
        }),
        [showDatasourceColumn, showTimestamps, showBannerColumn]
      );
      const visibilityRef = useRef(visibility);
      visibilityRef.current = visibility;

      const headerLabels = useMemo<Record<keyof ColumnWidths, string>>(
        () => ({
          timestamp: t('downloads.tab.retro.headers.timestamp'),
          banner: t('downloads.tab.retro.headers.banner', 'Banner'),
          app: t('downloads.tab.retro.headers.app'),
          datasource: t('downloads.tab.retro.headers.source'),
          events: t('downloads.tab.retro.headers.events'),
          depot: t('downloads.tab.retro.headers.depot'),
          client: t('downloads.tab.retro.headers.client'),
          speed: t('downloads.tab.retro.headers.avgSpeed'),
          cacheHit: t('downloads.tab.retro.headers.cachePerformance'),
          cacheMiss: t('downloads.tab.retro.headers.cachePerformance'),
          overall: t('downloads.tab.retro.headers.efficiency')
        }),
        [t]
      );

      // Pre-formatted strings for canvas-based column measurement - mirrors
      // exactly what RetroRow renders so fitted widths match the real cells.
      // Canvas measuring avoids forced DOM reflows on every data load.
      const measureRows = useMemo<RetroMeasureRow[]>(
        () =>
          groupedItems.map((data) => {
            const totalBytes = data.totalBytes || 0;
            const hitPercent = totalBytes > 0 ? (data.cacheHitBytes / totalBytes) * 100 : 0;
            const missPercent = totalBytes > 0 ? (data.cacheMissBytes / totalBytes) * 100 : 0;
            const detection = resolveGameDetection(
              data.gameAppId,
              data.gameName,
              detectionLookup,
              detectionByName,
              data.service,
              detectionByService
            );
            const onDiskSizeBytes = detection?.total_size_bytes;
            return {
              timeLines: formatTimeRangeLines(data.startTimeUtc, data.endTimeUtc),
              appName: data.gameName || data.service,
              serviceBadge: data.service.toUpperCase(),
              evictionLabel: data.isPartiallyEvicted
                ? 'Partially Evicted'
                : data.isEvicted
                  ? 'Evicted'
                  : '',
              onDiskLabel: onDiskSizeBytes
                ? t('dashboard.downloadsPanel.onDisk', { size: formatBytes(onDiskSizeBytes) })
                : '',
              datasourceLabel: data.datasource || t('downloads.tab.retro.notAvailable'),
              depotLabel:
                data.depotsSet.size > 1
                  ? t('downloads.tab.retro.depotCount', { count: data.depotsSet.size })
                  : data.depotId
                    ? String(data.depotId)
                    : t('downloads.tab.retro.notAvailable'),
              clientLabel:
                data.clientsSet.size > 1
                  ? t('downloads.tab.retro.clientCount', { count: data.clientsSet.size })
                  : data.clientIp,
              clientSubLabel:
                data.requestCount > 1
                  ? t('downloads.tab.retro.requestCount', { count: data.requestCount })
                  : '',
              speedLabel: formatSpeed(data.averageBytesPerSecond),
              hitLabel: `${formatBytes(data.cacheHitBytes)} (${formatPercent(hitPercent)})`,
              missLabel: `${formatBytes(data.cacheMissBytes)} (${formatPercent(missPercent)})`
            };
          }),
        [groupedItems, t, detectionLookup, detectionByName, detectionByService]
      );

      // Column widths: auto-fit measured content to the available table width
      // by default; manual once the user drags a divider or double-click-fits
      // a column (persisted). The "Fit columns" toolbar action clears saved
      // widths and returns to responsive auto-fit mode.
      const [columnWidths, setColumnWidths] = useState<ColumnWidths>(() => {
        try {
          const saved = localStorage.getItem(RETRO_WIDTHS_STORAGE_KEY);
          if (saved) {
            return { ...getDefaultColumnWidths(), ...JSON.parse(saved) };
          }
        } catch {
          // Ignore localStorage errors
        }
        return getDefaultColumnWidths();
      });
      const [isManualWidths, setIsManualWidths] = useState<boolean>(() => {
        try {
          return localStorage.getItem(RETRO_WIDTHS_STORAGE_KEY) !== null;
        } catch {
          return false;
        }
      });
      // Ref that mirrors columnWidths so handlers can read the current value
      // without being recreated on every width change.
      const columnWidthsRef = useRef<ColumnWidths>(columnWidths);
      columnWidthsRef.current = columnWidths;

      const persistWidths = useCallback((widths: ColumnWidths) => {
        try {
          localStorage.setItem(RETRO_WIDTHS_STORAGE_KEY, JSON.stringify(widths));
        } catch {
          // Ignore localStorage errors
        }
      }, []);

      // Container ref for measurements
      const containerRef = useRef<HTMLDivElement>(null);
      const fadeContainerRef = useRef<HTMLDivElement>(null);

      // Live container width. The view can mount hidden (display:none keeps it
      // warm across view switches), so the real width arrives via
      // ResizeObserver when the container becomes visible - that observation
      // re-triggers auto-fit after unhide and on window/layout resizes.
      const [containerWidth, setContainerWidth] = useState(0);
      useEffect(() => {
        const el = containerRef.current;
        if (!el) return;
        const update = (width: number) =>
          setContainerWidth((prev) => (Math.abs(prev - width) < 1 ? prev : Math.round(width)));
        update(el.clientWidth);
        const observer = new ResizeObserver((entries) => {
          for (const entry of entries) {
            update(entry.contentRect.width);
          }
        });
        observer.observe(el);
        return () => observer.disconnect();
      }, []);

      // Drag handling: mousemove only rewrites the --retro-grid-cols CSS
      // variable on the container (no React re-render); state commits on
      // mouseup so persistence and memoized rows stay cheap.
      const handleMouseDown = useCallback(
        (column: keyof ColumnWidths, e: React.MouseEvent) => {
          e.preventDefault();
          e.stopPropagation();

          const startX = e.clientX;
          const startWidth = columnWidthsRef.current[column];
          let liveWidths = columnWidthsRef.current;

          const handleMouseMove = (moveEvent: MouseEvent) => {
            const diff = moveEvent.clientX - startX;
            const newWidth = Math.max(RESIZE_MIN_WIDTH, startWidth + diff);
            liveWidths = { ...liveWidths, [column]: newWidth };
            containerRef.current?.style.setProperty(
              '--retro-grid-cols',
              buildGridTemplate(liveWidths, visibilityRef.current)
            );
          };

          const handleMouseUp = () => {
            document.removeEventListener('mousemove', handleMouseMove);
            document.removeEventListener('mouseup', handleMouseUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            setColumnWidths(liveWidths);
            setIsManualWidths(true);
            persistWidths(liveWidths);
          };

          document.addEventListener('mousemove', handleMouseMove);
          document.addEventListener('mouseup', handleMouseUp);
          document.body.style.cursor = 'col-resize';
          document.body.style.userSelect = 'none';
        },
        [persistWidths]
      );

      // "Fit columns" toolbar action: drop any manual widths and fit the
      // measured content widths into the table's current rendered width.
      const handleResetWidths = useCallback(() => {
        try {
          localStorage.removeItem(RETRO_WIDTHS_STORAGE_KEY);
        } catch {
          // Ignore localStorage errors
        }
        setIsManualWidths(false);

        const measured =
          measureRows.length > 0
            ? measureAllRetroColumns(measureRows, headerLabels, visibility)
            : getDefaultColumnWidths();
        const width = containerRef.current?.clientWidth;
        if (width && width > 0) {
          setColumnWidths(fitWidthsToContainer(measured, width, visibility));
          return;
        }

        setColumnWidths(measured);
      }, [measureRows, headerLabels, visibility]);

      // Double-click on a divider: fit that one column to its content. An
      // explicit per-column override, so widths switch to manual mode.
      const handleAutoFitColumn = useCallback(
        (column: keyof ColumnWidths) => {
          const requiredWidth = measureRetroColumn(column, measureRows, headerLabels[column]);
          let next: ColumnWidths = { ...columnWidthsRef.current, [column]: requiredWidth };
          const width = containerRef.current?.clientWidth;
          if (width) {
            next = fitWidthsToContainer(next, width, visibility, { [column]: requiredWidth });
          }
          setColumnWidths(next);
          setIsManualWidths(true);
          persistWidths(next);
        },
        [measureRows, headerLabels, visibility, persistWidths]
      );

      const setPageFading = useCallback((fading: boolean) => {
        fadeContainerRef.current?.classList.toggle('page-fading', fading);
      }, []);

      // Auto-fit columns to the current rows and available width before paint.
      // Reruns on every page/filter/data change and on container resizes until
      // the user takes manual control; useLayoutEffect keeps the fit in the
      // same frame as the row update, so columns never visibly jump after
      // render.
      useLayoutEffect(() => {
        if (isManualWidths) return;
        // containerWidth 0 = mounted but hidden (display:none view switch);
        // the ResizeObserver re-fires this fit when the table becomes visible.
        if (containerWidth <= 0 || measureRows.length === 0) return;
        const measured = measureAllRetroColumns(measureRows, headerLabels, visibility);
        const next = fitWidthsToContainer(measured, containerWidth, visibility);
        setColumnWidths((prev) => (widthsEqual(prev, next) ? prev : next));
      }, [isManualWidths, containerWidth, measureRows, headerLabels, visibility]);

      // Expose imperative helpers to parent via ref
      useImperativeHandle(
        ref,
        () => ({
          resetWidths: handleResetWidths,
          setPageFading
        }),
        [handleResetWidths, setPageFading]
      );

      // Server mode: fade the table while a follow-up page/filter fetch is in
      // flight (previous rows stay visible via keep-previous-data).
      useEffect(() => {
        if (!serverMode) return;
        setPageFading(serverRetro.isFetching && !serverRetro.isLoading);
      }, [serverMode, serverRetro.isFetching, serverRetro.isLoading, setPageFading]);

      const handleImageError = useCallback((gameAppId: string) => {
        setImageErrors((prev) => new Set(prev).add(gameAppId));
      }, []);

      // Fetch event associations for visible downloads
      // refreshVersion triggers re-fetch when cache is invalidated (e.g., DownloadTagged event)
      useEffect(() => {
        const allDownloadIds = groupedItems.flatMap((group) => group.downloadIds);
        if (allDownloadIds.length > 0) {
          fetchAssociations(allDownloadIds);
        }
      }, [groupedItems, fetchAssociations, refreshVersion]);

      // Pre-compute row data with events to avoid recalculating during render
      // This memoization prevents expensive event lookups on every render
      const rowsWithEvents = useMemo<RetroRowData[]>(() => {
        return groupedItems.map((data) => {
          // Aggregate events for this depot group
          const eventsMap = new Map<number, EventSummary>();
          data.downloadIds.forEach((id) => {
            const associations = getAssociations(id);
            associations.events.forEach((event) => {
              if (!eventsMap.has(event.id)) {
                eventsMap.set(event.id, event);
              }
            });
          });
          const events = Array.from(eventsMap.values());

          // Pre-calculate derived values
          const totalBytes = data.totalBytes || 0;
          const cacheHitBytes = data.cacheHitBytes || 0;
          const cacheMissBytes = data.cacheMissBytes || 0;
          const hitPercent = totalBytes > 0 ? (cacheHitBytes / totalBytes) * 100 : 0;
          const timeLines = formatTimeRangeLines(data.startTimeUtc, data.endTimeUtc);
          const timeRangeTitle = formatTimeRange(data.startTimeUtc, data.endTimeUtc);

          // Check if has game image
          const serviceLower = (data.service ?? '').toLowerCase();
          const isSteam = serviceLower === 'steam';
          const isEpicService = serviceLower === 'epic' || serviceLower === 'epicgames';
          const hasSteamImage =
            !aestheticMode &&
            isSteam &&
            data.gameAppId &&
            availableImages.has(String(data.gameAppId)) &&
            !imageErrors.has(String(data.gameAppId));
          const hasEpicImage =
            !aestheticMode &&
            isEpicService &&
            data.epicAppId &&
            availableImages.has(data.epicAppId) &&
            !imageErrors.has(`epic-${data.epicAppId}`);
          const nameKeyed = nameKeyedImageKey(data.service, data.gameName);
          const hasNameKeyedImage =
            !aestheticMode &&
            nameKeyed !== null &&
            availableImages.has(nameKeyed.slug) &&
            !imageErrors.has(`${nameKeyed.service}-${nameKeyed.slug}`);
          const hasGameImage = Boolean(hasSteamImage || hasEpicImage || hasNameKeyedImage);

          const detection = resolveGameDetection(
            data.gameAppId,
            data.gameName,
            detectionLookup,
            detectionByName,
            data.service,
            detectionByService
          );

          return {
            ...data,
            events,
            totalBytes,
            cacheHitBytes,
            cacheMissBytes,
            hitPercent,
            timeLines,
            timeRangeTitle,
            hasGameImage,
            nameKeyedService: hasNameKeyedImage ? nameKeyed!.service : null,
            nameKeyedSlug: hasNameKeyedImage ? nameKeyed!.slug : null,
            onDiskSizeBytes: detection?.total_size_bytes ?? null
          };
        });
      }, [
        groupedItems,
        getAssociations,
        aestheticMode,
        imageErrors,
        availableImages,
        detectionLookup,
        detectionByName,
        detectionByService
      ]);

      // Grid template applied as a CSS variable on the table container; the
      // header and every row consume it through the .retro-grid-row class.
      const gridTemplate = useMemo(
        () => buildGridTemplate(columnWidths, visibility),
        [columnWidths, visibility]
      );

      const shouldVirtualize = rowsWithEvents.length > RETRO_VIRTUALIZATION_THRESHOLD;
      const virtualParentRef = useRef<HTMLDivElement | null>(null);
      const rowVirtualizer = useVirtualizer({
        count: shouldVirtualize ? rowsWithEvents.length : 0,
        getScrollElement: () => virtualParentRef.current,
        estimateSize: () => (isDesktop ? 90 : 200),
        overscan: 5,
        measureElement: (el) => el?.getBoundingClientRect().height ?? (isDesktop ? 90 : 200)
      });

      const renderRow = (
        data: RetroRowData,
        rowIndex: number,
        virtualAttrs?: {
          dataIndex: number;
          measureRef: (el: Element | null) => void;
          translateY: number;
        }
      ) => (
        <RetroRow
          key={data.id}
          data={data}
          rowIndex={rowIndex}
          isDesktop={isDesktop}
          showTimestamps={showTimestamps}
          showBannerColumn={showBannerColumn}
          showDatasourceColumn={showDatasourceColumn}
          showDatasourceBadge={showDatasourceColumn}
          onImageError={handleImageError}
          dataIndex={virtualAttrs?.dataIndex}
          measureRef={virtualAttrs?.measureRef}
          translateY={virtualAttrs?.translateY}
        />
      );

      // Header cells align with their column content: text columns left,
      // numeric readouts right, badges/bars/gauge centered.
      const headerCell = (
        column: keyof ColumnWidths,
        options: { align?: 'left' | 'center' | 'right'; resizable?: boolean } = {}
      ) => {
        const { align = 'center', resizable = true } = options;
        const justifyClass =
          align === 'left'
            ? ' justify-start'
            : align === 'right'
              ? ' justify-end'
              : ' justify-center';
        const textClass =
          align === 'left' ? ' text-left' : align === 'right' ? ' text-right' : ' text-center';
        return (
          <div
            className={`relative px-2 flex items-center h-full min-w-0${justifyClass}`}
            data-header
          >
            <span className={`min-w-0 flex-1 truncate${textClass}`}>{headerLabels[column]}</span>
            {resizable && (
              <ResizeHandle
                onMouseDown={(e: React.MouseEvent) => handleMouseDown(column, e)}
                onDoubleClick={() => handleAutoFitColumn(column)}
              />
            )}
          </div>
        );
      };

      return (
        <>
          {/* Pagination controls */}
          {totalPages > 1 && (
            <div className="pagination-sticky">
              <div className="p-2 rounded-lg bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)]">
                <Pagination
                  currentPage={currentPage}
                  totalPages={totalPages}
                  totalItems={totalItems}
                  itemsPerPage={itemsPerPage}
                  onPageChange={onPageChange}
                  itemLabel="depot groups"
                  showCard={false}
                  compact={!isDesktop}
                />
              </div>
            </div>
          )}

          {/* Surface fetch failures instead of silently showing stale rows */}
          {serverMode && serverRetro.error && (
            <Alert color="red">{t('downloads.tab.retro.loadError')}</Alert>
          )}

          <div ref={fadeContainerRef} className="page-content-transition relative z-0">
            <div
              ref={containerRef}
              className="rounded-lg border border-[var(--theme-border-primary)] overflow-x-auto retro-table-container bg-[var(--theme-card-bg)]"
              style={{ '--retro-grid-cols': gridTemplate } as React.CSSProperties}
            >
              <div>
                {/* Desktop Table Header - only rendered on desktop via JS conditional */}
                {isDesktop && (
                  <div className="retro-grid-row retro-header-row select-none min-w-fit">
                    {showTimestamps && headerCell('timestamp', { align: 'left' })}
                    {showBannerColumn && headerCell('banner')}
                    {headerCell('app', { align: 'left' })}
                    {showDatasourceColumn && headerCell('datasource')}
                    {headerCell('events')}
                    {headerCell('depot', { align: 'right' })}
                    {headerCell('client', { align: 'right' })}
                    {headerCell('speed', { align: 'right' })}
                    {headerCell('cacheHit')}
                    {headerCell('overall', { resizable: false })}
                  </div>
                )}

                {/* Table Body */}
                {rowsWithEvents.length > 0 ? (
                  shouldVirtualize ? (
                    <div
                      ref={virtualParentRef}
                      className="virtual-list-parent virtual-list-parent-retro"
                    >
                      <div
                        className="virtual-list-inner"
                        style={{ height: `${rowVirtualizer.getTotalSize()}px` }}
                      >
                        {rowVirtualizer.getVirtualItems().map((virtualRow) =>
                          renderRow(rowsWithEvents[virtualRow.index], virtualRow.index, {
                            dataIndex: virtualRow.index,
                            measureRef: rowVirtualizer.measureElement,
                            translateY: virtualRow.start
                          })
                        )}
                      </div>
                    </div>
                  ) : (
                    <div>{rowsWithEvents.map((data, index) => renderRow(data, index))}</div>
                  )
                ) : serverMode && (serverRetro.isLoading || serverRetro.isFetching) ? (
                  <RetroSkeletonRows isDesktop={isDesktop} visibility={visibility} />
                ) : (
                  <EmptyState />
                )}
              </div>
            </div>
          </div>
        </>
      );
    }
  )
);

RetroView.displayName = 'RetroView';

export default RetroView;
