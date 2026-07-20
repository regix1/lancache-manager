import React, { useState, useCallback, useEffect, useRef, useMemo } from 'react';
import './StorageSection.css';
import { useTranslation } from 'react-i18next';
import {
  RefreshCw,
  AlertTriangle,
  Archive,
  Sliders,
  Database,
  Search,
  RotateCcw,
  Trash2,
  ChevronsDownUp,
  ChevronsUpDown
} from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Checkbox } from '@components/ui/Checkbox';
import { LoadingState, ReadOnlyBadge } from '@components/ui/ManagerCard';
import { AccordionSection } from '@components/ui/AccordionSection';
import Badge from '@components/ui/Badge';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { ActionMenuItem, ActionMenuDangerItem, ActionMenuDivider } from '@components/ui/ActionMenu';
import HighlightGlow from '@components/ui/HighlightGlow';
import { type AuthMode } from '@services/auth.service';
import { DirectoryPermissionsProvider } from '@contexts/DirectoryPermissionsProvider';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import { useTimeoutCallback } from '@/hooks/useTimeoutCallback';
import { useConfig } from '@contexts/useConfig';
import { ImageCacheContext, ImageInvalidateContext } from '@components/common/ImageCacheContext';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { useNotifications } from '@contexts/notifications';
import { useErrorHandler } from '@/hooks/useErrorHandler';
import { buildSeededRunningNotification } from '@contexts/notifications/seedOperationNotification';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { useCacheRemovalActive } from '@hooks/useCacheRemovalActive';
import { useDiskObjectCapability } from '@hooks/useDiskObjectCapability';
import { DiskObjectActionGate } from '@components/features/management/DiskObjectActionGate';
import { NginxReopenActionGate } from '@components/features/management/NginxReopenActionGate';
import CardDirectoryNotice from '@components/features/management/CardDirectoryNotice';
import { useSelectionSet, type SelectionSet, type SelectionAdapter } from '@/hooks/useSelectionSet';
import { useBulkRemoval, type EvictedQueueEntry } from '@contexts/BulkRemovalContext';
import CacheRemovalModal from '@components/modals/cache/CacheRemovalModal';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import EvictedItemsList from '../game-detection/EvictedItemsList';
import DatasourcesManager from '../datasources/DatasourcesInfo';
import LogRemovalManager from '../log-processing/LogRemovalManager';
import CacheManager from '../cache/CacheManager';
import CorruptionManager from '../cache/CorruptionManager';
import GameCacheDetector from '../game-detection/GameCacheDetector';
import {
  MANAGEMENT_STORAGE_KEYS,
  EVICTION_SETTINGS_CHANGED_EVENT,
  type EvictionSettingsChangedDetail
} from './managementStorageKeys';
import { getEvictedGames, getEvictedServices } from '../game-detection/cacheEntityFilters';
import { getGameUniqueId } from '../game-detection/gameUtils';
import {
  CACHED_DETECTION_RELOAD_DELAY_MS,
  loadCachedDetectionSnapshot,
  type CacheRemovalTarget
} from '../game-detection/cacheDetectionData';
import {
  runTrackedGameRemoval,
  runTrackedServiceRemoval,
  useCompletedRemovalPruning,
  useScheduledRemovalRefresh
} from '../game-detection/cacheRemovalHelpers';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';
import { FAILED_TO_REMOVE_GAME_I18N_KEY } from '@contexts/notifications/constants';
import { getNginxReopenGateForEntities } from '@utils/nginxReopenAvailability';
import { isCardDiskActionBlocked, resolveCardNotice } from '@utils/cardDirectoryNotice';

// Adapts the combined evicted selection set (prefixed keyspace) into the raw-keyed
// SelectionAdapter each list expects, translating keys through the given prefix.
function scopedSelection(selection: SelectionSet<string>, prefix: string): SelectionAdapter {
  return {
    isSelected: (key: string) => selection.isSelected(`${prefix}${key}`),
    onToggle: (key: string) => selection.toggle(`${prefix}${key}`),
    allSelected: (keys: string[]) =>
      keys.length > 0 && keys.every((k) => selection.isSelected(`${prefix}${k}`)),
    setMany: (keys: string[], selected: boolean) =>
      selection.setMany(
        keys.map((k) => `${prefix}${k}`),
        selected
      )
  };
}

interface StorageSectionProps {
  isAdmin: boolean;
  authMode: AuthMode;
  mockMode: boolean;
  gameCacheRefreshKey: number;
  highlightEviction?: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
  onDataRefresh: () => void;
}

const StorageSectionContent: React.FC<StorageSectionProps> = ({
  isAdmin,
  authMode,
  mockMode,
  gameCacheRefreshKey,
  highlightEviction = false,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  const { t } = useTranslation();
  const {
    logsReadOnly,
    cacheReadOnly,
    logsExist,
    cacheExist,
    checkingPermissions,
    reload: reloadPermissions
  } = useDirectoryPermissionsContext();
  const { config } = useConfig();
  const datasources =
    config.dataSources && config.dataSources.length > 0
      ? config.dataSources
      : [
          {
            name: 'default',
            cachePath: config.cachePath,
            logsPath: config.logsPath,
            cacheWritable: config.cacheWritable,
            logsWritable: config.logsWritable,
            enabled: true,
            layout: 'monolithic' as const,
            nginxReopenAvailable: false
          }
        ];
  const [isRechecking, setIsRechecking] = useState(false);

  // Image cache busting for GameCacheDetector's GameImage components
  const [imageCacheVersion, setImageCacheVersion] = useState(() => Date.now());
  const invalidateImageCache = useCallback(() => setImageCacheVersion(Date.now()), []);

  // Eviction Settings State
  const [evictionMode, setEvictionMode] = useState<string>('show');
  const [savedEvictionMode, setSavedEvictionMode] = useState<string>('show');
  const [pruneOrphanedDownloads, setPruneOrphanedDownloads] = useState(false);
  const [savedPruneOrphanedDownloads, setSavedPruneOrphanedDownloads] = useState(false);
  const [evictionLoading, setEvictionLoading] = useState(false);
  const [evictionSaving, setEvictionSaving] = useState(false);
  const [showRemoveConfirm, setShowRemoveConfirm] = useState(false);
  const [isStartingEvictionScan, setIsStartingEvictionScan] = useState(false);
  const evictionScanInFlightRef = useRef(false);
  const [resettingEvictions, setResettingEvictions] = useState(false);
  const isEvictionDirty =
    evictionMode !== savedEvictionMode || pruneOrphanedDownloads !== savedPruneOrphanedDownloads;

  const { notifications, addNotification, updateNotification } = useNotifications();
  const { notifyError } = useErrorHandler();

  // Local state for evicted items - same pattern as GameCacheDetector's games/services.
  // Items only disappear when explicitly filtered by notification completion.
  // StorageSection needs the FULL GameCacheInfo shape (depot_ids, sample_urls,
  // cache_file_paths) to render cards correctly, so we fetch directly from
  // /api/games/cached-detection instead of the slim dashboard batch context.
  const [evictedGames, setEvictedGames] = useState<GameCacheInfo[]>([]);
  const [evictedServices, setEvictedServices] = useState<ServiceCacheInfo[]>([]);
  const allEvictedNginxReopenGate = getNginxReopenGateForEntities(datasources, [
    ...evictedServices,
    ...evictedGames
  ]);
  const allEvictedNginxReopenMessage = allEvictedNginxReopenGate.messageKey
    ? t(allEvictedNginxReopenGate.messageKey)
    : '';
  const directoryNoticeConditions = {
    cacheWrite: true,
    cacheRead: false,
    logsWrite: true,
    nginx: true
  };
  const directoryNoticeLiveState = {
    cacheReadOnly,
    logsReadOnly,
    cacheExist,
    logsExist,
    checkingPermissions,
    nginxReopenGate: allEvictedNginxReopenGate
  };
  const directoryNotice = resolveCardNotice(directoryNoticeConditions, directoryNoticeLiveState);
  const diskActionBlocked = isCardDiskActionBlocked(
    directoryNoticeConditions,
    directoryNoticeLiveState
  );

  const isAnyEvictedRemovalRunning = useCacheRemovalActive();
  // Eviction removal deletes evicted cache files, which needs the monolithic cache-key recipe;
  // an all bare-metal fleet cannot map objects to files, so these actions are disabled.
  const diskObjectsAvailable = useDiskObjectCapability();

  // Managed setTimeout for post-SignalR eviction refetch; cancels on unmount.
  const scheduleEvictedItemsRefresh = useTimeoutCallback(CACHED_DETECTION_RELOAD_DELAY_MS);
  const scheduleRemovalRefresh = useScheduledRemovalRefresh();

  // Fetch full detection data (fat shape) for evicted items rendering
  const fetchEvictedItems = useCallback(async () => {
    try {
      const snapshot = await loadCachedDetectionSnapshot();
      const games = getEvictedGames(snapshot.games);
      const services = getEvictedServices(snapshot.services);
      setEvictedGames(games);
      setEvictedServices(services);
    } catch (err) {
      // Background refresh (mount + post-removal/scan); the section already renders an empty
      // evicted-items state, so a transient failure here is explicit background noise.
      notifyError(
        t('management.storage.errors.fetchEvictedItems', 'Failed to fetch evicted items'),
        err,
        {
          silent: true,
          logLabel: 'Failed to fetch evicted items'
        }
      );
    }
  }, [notifyError, t]);

  // Sync from API when refresh key changes - but NOT during active removal
  useEffect(() => {
    if (isAnyEvictedRemovalRunning) return;
    void fetchEvictedItems();
  }, [fetchEvictedItems, gameCacheRefreshKey, isAnyEvictedRemovalRunning]);

  // StorageSection owns independent evictedGames/evictedServices state from
  // GameCacheDetector, so the GameCacheDetector SignalR listener does not
  // refresh this list. Without a listener here, an eviction scan that flips
  // Downloads.IsEvicted would update the DB (and evicted_downloads_count on
  // the detection response), but the Evicted Items card would keep showing
  // whatever it loaded at mount - hence "14 newly evicted" in the logs with
  // no visible UI change. A successful cache clear now performs trusted eviction
  // reconciliation too, so refetch on clear / scan / detection completion.
  const { on, off } = useSignalR();
  useEffect(() => {
    const handleScanDone = () => {
      if (isAnyEvictedRemovalRunning) return;
      // Small delay so the backend finishes its post-scan recovery + cache
      // invalidation before we refetch. The hook owns cleanup on unmount.
      scheduleEvictedItemsRefresh(() => void fetchEvictedItems());
    };
    on('CacheClearingComplete', handleScanDone);
    on('EvictionScanComplete', handleScanDone);
    on('GameDetectionComplete', handleScanDone);
    return () => {
      off('CacheClearingComplete', handleScanDone);
      off('EvictionScanComplete', handleScanDone);
      off('GameDetectionComplete', handleScanDone);
    };
  }, [on, off, fetchEvictedItems, isAnyEvictedRemovalRunning, scheduleEvictedItemsRefresh]);

  // Track partial eviction target so we know which item to filter on eviction_removal completion
  const partialRemovalTargetRef = useRef<CacheRemovalTarget | null>(null);

  // Remove evicted items from local state when notification confirms removal is done
  // (identical pattern to GameCacheDetector lines 216-241)
  useCompletedRemovalPruning({
    notifications,
    setGames: setEvictedGames,
    setServices: setEvictedServices,
    partialRemovalTargetRef
  });

  // Evicted removal state (migrated from GameCacheDetector)
  const [evictedGameToRemove, setEvictedGameToRemove] = useState<GameCacheInfo | null>(null);
  const [partialEvictedTarget, setPartialEvictedTarget] = useState<
    GameCacheInfo | ServiceCacheInfo | null
  >(null);
  const [evictedServiceToRemove, setEvictedServiceToRemove] = useState<ServiceCacheInfo | null>(
    null
  );

  const handleEvictedGameRemoveClick = (game: GameCacheInfo) => {
    if (!isAdmin) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('common.fullAuthRequired'),
        details: { notificationType: 'error' }
      });
      return;
    }
    if (game.is_evicted !== true && (game.evicted_downloads_count ?? 0) > 0) {
      setPartialEvictedTarget(game);
    } else {
      setEvictedGameToRemove(game);
    }
  };

  const handleEvictedServiceRemoveClick = (service: ServiceCacheInfo) => {
    if (!isAdmin) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('common.fullAuthRequired'),
        details: { notificationType: 'error' }
      });
      return;
    }
    if (service.is_evicted !== true && (service.evicted_downloads_count ?? 0) > 0) {
      setPartialEvictedTarget(service);
    } else {
      setEvictedServiceToRemove(service);
    }
  };

  // "Remove All evicted" is ONE batched backend operation (single access.log
  // rewrite covering every evicted entity + one DB transaction + one disk-summary
  // refresh) — not a per-entity loop. Progress, cancel, and page-refresh recovery
  // flow through the standard eviction_removal notification (bulk scope), so this
  // component only kicks the operation off. Post-run refreshes are event-driven:
  // EvictionRemovalComplete fires GameCacheDetector's listener (stats +
  // gameCacheRefreshKey bump), and the busy-flip of isAnyEvictedRemovalRunning
  // re-runs the fetchEvictedItems effect above.
  const handleRemoveAllEvicted = useCallback(async () => {
    setShowRemoveAllConfirm(false);
    if (!isAdmin || !allEvictedNginxReopenGate.available) return;
    if (evictedServices.length + evictedGames.length === 0) return;

    setRemoveAllRunning(true);
    try {
      const result = await ApiService.removeAllEvicted();
      // Seed the eviction_removal card with the 202's operationId so busy-tracking
      // and the cancel button work immediately instead of racing the SignalR
      // Started event (same pattern as the eviction-scan seed below).
      // Wait-queue model: queued/deduplicated responses must not seed a running card.
      if (result.operationId && !result.queued && !result.alreadyRunning) {
        addNotification(
          buildSeededRunningNotification(
            'eviction_removal',
            result.operationId,
            t('signalr.evictionRemove.starting.bulk')
          )
        );
      }
    } catch (err: unknown) {
      const errorMsg = getErrorMessage(err);
      console.error('Bulk evicted removal failed to start:', errorMsg);
      addNotification({
        type: 'generic',
        status: 'failed',
        message: errorMsg,
        details: { notificationType: 'error' }
      });
    } finally {
      // The eviction_removal notification owns busy-tracking from here
      // (isAnyEvictedRemovalRunning); the local flag only covers the kick-off.
      setRemoveAllRunning(false);
    }
  }, [
    addNotification,
    allEvictedNginxReopenGate.available,
    evictedGames.length,
    evictedServices.length,
    isAdmin,
    t
  ]);

  const confirmPartialEvictedRemoval = async () => {
    if (!partialEvictedTarget) return;

    const isService = 'service_name' in partialEvictedTarget;

    if (isService) {
      const service = partialEvictedTarget as ServiceCacheInfo;
      partialRemovalTargetRef.current = { serviceName: service.service_name };
      setPartialEvictedTarget(null);
      try {
        await ApiService.removeEvictedForService(service.service_name);
        scheduleRemovalRefresh(onDataRefresh);
      } catch (err: unknown) {
        const errorMsg =
          getErrorMessage(err) || t('management.gameDetection.failedToRemoveService');
        console.error('Partial evicted service removal error:', errorMsg);
        onError(errorMsg);
      }
    } else {
      const game = partialEvictedTarget as GameCacheInfo;
      const isEpic = game.service === 'epicgames';
      // Named (Blizzard/Riot) games have game_app_id === 0 and no Epic id; their identity is
      // (service, game_name). They have their own per-entity evicted scope
      // (cache/evicted/named/{service}/{gameName}), mirroring Steam/Epic partial-evicted removal:
      // only the evicted records/detection for THIS named game are removed.
      const isNamed =
        !isEpic && game.game_app_id === 0 && !!game.service && game.service !== 'steam';
      partialRemovalTargetRef.current = isEpic
        ? {
            epicAppId: game.epic_app_id ?? undefined,
            gameName: game.game_name
          }
        : isNamed
          ? { gameName: game.game_name }
          : { gameAppId: game.game_app_id };
      setPartialEvictedTarget(null);
      try {
        if (isEpic) {
          if (!game.epic_app_id) {
            partialRemovalTargetRef.current = null;
            onError(t(FAILED_TO_REMOVE_GAME_I18N_KEY));
            return;
          }
          await ApiService.removeEvictedForEpicGame(game.epic_app_id);
        } else if (isNamed) {
          await ApiService.removeEvictedForNamedGame(game.service!, game.game_name);
        } else {
          await ApiService.removeEvictedForGame(game.game_app_id);
        }
        scheduleRemovalRefresh(onDataRefresh);
      } catch (err: unknown) {
        const errorMsg = getErrorMessage(err) || t(FAILED_TO_REMOVE_GAME_I18N_KEY);
        console.error('Partial evicted game removal error:', errorMsg);
        onError(errorMsg);
      }
    }
  };

  const confirmEvictedGameRemoval = async () => {
    if (!evictedGameToRemove) return;

    const game = evictedGameToRemove;
    setEvictedGameToRemove(null);
    await runTrackedGameRemoval({
      game,
      t,
      addNotification,
      updateNotification,
      scheduleRemovalRefresh,
      onDataRefresh
    });
  };

  const confirmEvictedServiceRemoval = async () => {
    if (!evictedServiceToRemove) return;

    const service = evictedServiceToRemove;
    setEvictedServiceToRemove(null);
    await runTrackedServiceRemoval({
      service,
      t,
      addNotification,
      updateNotification,
      scheduleRemovalRefresh,
      onDataRefresh
    });
  };

  const isEvictionScanNotificationRunning = useOperationBusy({
    types: ['eviction_scan'],
    status: ['running', 'waiting']
  });
  const isEvictionRemovalRunning = useOperationBusy({
    types: ['eviction_removal'],
    status: ['running', 'waiting']
  });
  // Wait-queue model: an eviction REMOVAL no longer disables the scan button (the scan
  // queues behind it - purple card feedback). Same-op only: scan already running or the
  // kick-off request in flight.
  const isEvictionScanRunning = isEvictionScanNotificationRunning || isStartingEvictionScan;

  const [evictedDataExpanded, setEvictedDataExpanded] = useState(() => {
    const saved = localStorage.getItem(MANAGEMENT_STORAGE_KEYS.EVICTED_DATA_EXPANDED);
    return saved !== null ? saved === 'true' : false;
  });

  useEffect(() => {
    localStorage.setItem(
      MANAGEMENT_STORAGE_KEYS.EVICTED_DATA_EXPANDED,
      String(evictedDataExpanded)
    );
  }, [evictedDataExpanded]);

  const [evictionSettingsExpanded, setEvictionSettingsExpanded] = useState(() => {
    const saved = localStorage.getItem(MANAGEMENT_STORAGE_KEYS.EVICTION_SETTINGS_EXPANDED);
    return saved !== null ? saved === 'true' : true;
  });

  useEffect(() => {
    localStorage.setItem(
      MANAGEMENT_STORAGE_KEYS.EVICTION_SETTINGS_EXPANDED,
      String(evictionSettingsExpanded)
    );
  }, [evictionSettingsExpanded]);

  const [evictedItemsExpanded, setEvictedItemsExpanded] = useState(() => {
    const saved = localStorage.getItem(MANAGEMENT_STORAGE_KEYS.EVICTED_ITEMS_EXPANDED);
    return saved !== null ? saved === 'true' : true;
  });

  useEffect(() => {
    localStorage.setItem(
      MANAGEMENT_STORAGE_KEYS.EVICTED_ITEMS_EXPANDED,
      String(evictedItemsExpanded)
    );
  }, [evictedItemsExpanded]);

  // "Remove All" state - sequential per-item eviction removal. One at a time
  // mirrors the per-item Remove flow (each item gets its own SignalR operation,
  // its own log-purge, its own progress bar) so the user sees exactly what's
  // happening and a single failure doesn't abort the rest.
  const [showRemoveAllConfirm, setShowRemoveAllConfirm] = useState(false);
  const [removeAllRunning, setRemoveAllRunning] = useState(false);
  // Kick-off flag for the "Remove Selected" evicted batch, kept separate from the
  // "Remove All" kick-off flag so a selected run does not spin the Remove All button.
  const [removeSelectedEvictedRunning, setRemoveSelectedEvictedRunning] = useState(false);

  // Client-only multi-select for the combined evicted games + services list.
  // ONE set covers both kinds via collision-safe prefixes ('svc::' service_name,
  // 'game::' getGameUniqueId) so a single "Remove Selected" removes any mix.
  const evictedSelection: SelectionSet<string> = useSelectionSet<string>();
  const { runEvictedRemoval, isEvictedRemovalRunning } = useBulkRemoval();
  const [confirmRemoveSelectedEvicted, setConfirmRemoveSelectedEvicted] = useState(false);

  // Selected items derived from the current evicted lists so a stale key never
  // contributes to the count or the batch (the prune effect below is hygiene).
  const selectedEvictedServices = useMemo(
    () => evictedServices.filter((s) => evictedSelection.isSelected(`svc::${s.service_name}`)),
    [evictedServices, evictedSelection]
  );
  const selectedEvictedGames = useMemo(
    () => evictedGames.filter((g) => evictedSelection.isSelected(`game::${getGameUniqueId(g)}`)),
    [evictedGames, evictedSelection]
  );
  const selectedEvictedCount = selectedEvictedServices.length + selectedEvictedGames.length;
  const selectedEvictedNginxReopenGate = getNginxReopenGateForEntities(datasources, [
    ...selectedEvictedServices,
    ...selectedEvictedGames
  ]);
  const selectedEvictedNginxReopenMessage = selectedEvictedNginxReopenGate.messageKey
    ? t(selectedEvictedNginxReopenGate.messageKey)
    : '';

  // Prune selection keys that dropped out of the evicted lists on refresh (plan §6).
  const evictedKeySignature = [
    ...evictedServices.map((s) => `svc::${s.service_name}`),
    ...evictedGames.map((g) => `game::${getGameUniqueId(g)}`)
  ].join('|');
  useEffect(() => {
    const valid = new Set([
      ...evictedServices.map((s) => `svc::${s.service_name}`),
      ...evictedGames.map((g) => `game::${getGameUniqueId(g)}`)
    ]);
    const stale = [...evictedSelection.selected].filter((k) => !valid.has(k));
    if (stale.length > 0) evictedSelection.setMany(stale, false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [evictedKeySignature]);

  // Prefixed adapters: CacheEntityList keys rows by raw service_name /
  // getGameUniqueId, so translate to the combined set's prefixed keyspace.
  const evictedServicesSelectionProp = useMemo(
    () => scopedSelection(evictedSelection, 'svc::'),
    [evictedSelection]
  );
  const evictedGamesSelectionProp = useMemo(
    () => scopedSelection(evictedSelection, 'game::'),
    [evictedSelection]
  );

  const handleRemoveSelectedEvicted = useCallback(async () => {
    setConfirmRemoveSelectedEvicted(false);
    if (!isAdmin || !selectedEvictedNginxReopenGate.available) return;
    const items: EvictedQueueEntry[] = [
      ...selectedEvictedServices.map((service) => ({ kind: 'service' as const, service })),
      ...selectedEvictedGames.map((game) => ({ kind: 'game' as const, game }))
    ];
    if (items.length === 0) return;
    await runEvictedRemoval(items, {
      onRunningChange: setRemoveSelectedEvictedRunning,
      onSettled: () => {
        evictedSelection.clear();
        onDataRefresh();
      }
    });
  }, [
    isAdmin,
    selectedEvictedServices,
    selectedEvictedGames,
    runEvictedRemoval,
    evictedSelection,
    onDataRefresh,
    selectedEvictedNginxReopenGate.available
  ]);

  const evictionAllExpanded = evictionSettingsExpanded && evictedItemsExpanded;

  const handleEvictionExpandCollapseAll = () => {
    const next = !evictionAllExpanded;
    setEvictionSettingsExpanded(next);
    setEvictedItemsExpanded(next);
  };

  const loadEvictionSettings = useCallback(
    async (signal?: AbortSignal) => {
      setEvictionLoading(true);
      try {
        const response = await ApiService.getEvictionSettings(signal);
        setEvictionMode(response.evictedDataMode);
        setSavedEvictionMode(response.evictedDataMode);
        setPruneOrphanedDownloads(response.pruneOrphanedDownloads);
        setSavedPruneOrphanedDownloads(response.pruneOrphanedDownloads);
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        onError(t('management.sections.data.evictionLoadError'));
      } finally {
        setEvictionLoading(false);
      }
    },
    [onError, t]
  );

  useEffect(() => {
    const controller = new AbortController();
    loadEvictionSettings(controller.signal);
    return () => controller.abort();
  }, [loadEvictionSettings]);

  const performEvictionSave = async () => {
    setEvictionSaving(true);
    try {
      const response = await ApiService.updateEvictionSettings(
        evictionMode,
        undefined,
        pruneOrphanedDownloads
      );
      setEvictionMode(response.evictedDataMode);
      setSavedEvictionMode(response.evictedDataMode);
      setPruneOrphanedDownloads(response.pruneOrphanedDownloads);
      setSavedPruneOrphanedDownloads(response.pruneOrphanedDownloads);
      const detail: EvictionSettingsChangedDetail = {
        evictedDataMode: response.evictedDataMode,
        evictionScanNotifications: response.evictionScanNotifications
      };
      window.dispatchEvent(
        new CustomEvent<EvictionSettingsChangedDetail>(EVICTION_SETTINGS_CHANGED_EVENT, { detail })
      );
      onSuccess(t('management.sections.data.evictionSaveSuccess'));
      onDataRefresh();
    } catch (err: unknown) {
      onError(getErrorMessage(err) || t('management.sections.data.evictionSaveError'));
    } finally {
      setEvictionSaving(false);
    }
  };

  const handleSaveEviction = async () => {
    if (evictionMode === 'remove' && savedEvictionMode !== 'remove') {
      setShowRemoveConfirm(true);
      return;
    }
    await performEvictionSave();
  };

  const handleConfirmRemove = async () => {
    await performEvictionSave();
    setShowRemoveConfirm(false);
  };

  const isMountedRef = useRef(true);
  useEffect(() => {
    isMountedRef.current = true;
    return () => {
      isMountedRef.current = false;
    };
  }, []);

  const handleStartEvictionScan = async () => {
    if (evictionScanInFlightRef.current) return;
    evictionScanInFlightRef.current = true;
    setIsStartingEvictionScan(true);

    const attemptScan = async (): Promise<void> => {
      const result = await ApiService.startEvictionScan();
      // Wait-queue model: a queued/already-running response means the backend parked or
      // deduplicated the request - the OperationWaiting SignalR event (or the existing
      // card) owns the UI, so do NOT seed a running card over it.
      // Seed the scan card from the PERSISTED mode (savedEvictionMode), not the local radio
      // selection: the backend silences the scan phase based on its saved EvictedDataMode, so
      // seeding from an unsaved local 'show' while the server runs silent 'remove' would leave
      // a stuck running card (and vice versa would merely delay the bar by one SignalR event).
      if (
        result.operationId &&
        !result.queued &&
        !result.alreadyRunning &&
        savedEvictionMode !== 'remove'
      ) {
        addNotification(
          buildSeededRunningNotification(
            'eviction_scan',
            result.operationId,
            t('signalr.evictionScan.scanning')
          )
        );
      }
    };

    try {
      await attemptScan();
    } catch (err: unknown) {
      onError(getErrorMessage(err));
    } finally {
      if (isMountedRef.current) {
        setIsStartingEvictionScan(false);
      }
      evictionScanInFlightRef.current = false;
    }
  };

  const handleResetEvictions = async () => {
    setResettingEvictions(true);
    try {
      const result = await ApiService.resetEvictions();
      onSuccess(
        t('management.sections.data.resetEvictionsSuccess', {
          count: result.reset
        })
      );
      onDataRefresh();
    } catch (err: unknown) {
      onError(getErrorMessage(err) || t('management.sections.data.resetEvictionsError'));
    } finally {
      setResettingEvictions(false);
    }
  };

  const handleRecheckPermissions = async () => {
    setIsRechecking(true);
    try {
      await reloadPermissions();
    } finally {
      setIsRechecking(false);
    }
  };

  // Only show the recheck button when at least one directory is read-only
  const hasPermissionIssues = logsReadOnly || cacheReadOnly;

  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-storage"
      aria-labelledby="tab-storage"
    >
      {/* Section Header */}
      <div className="mb-4 sm:mb-6">
        <div className="flex flex-wrap items-center justify-end gap-3">
          {hasPermissionIssues && (
            <Button
              variant="filled"
              color="gray"
              size="md"
              onClick={handleRecheckPermissions}
              disabled={isRechecking}
            >
              {isRechecking ? (
                <LoadingSpinner inline size="sm" className="mr-1.5" />
              ) : (
                <RefreshCw className="h-3.5 w-3.5 mr-1.5" />
              )}
              {isRechecking
                ? t('management.sections.storage.recheckingPermissions')
                : t('management.sections.storage.recheckPermissions')}
            </Button>
          )}
        </div>
      </div>

      {/* ==================== LOG OPERATIONS ==================== */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-blue)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.storage.logOperations')}
          </h3>
        </div>

        <div className="space-y-4">
          {/* Log Processing */}
          <DatasourcesManager
            isAdmin={isAdmin}
            mockMode={mockMode}
            onError={onError}
            onSuccess={onSuccess}
            onDataRefresh={onDataRefresh}
          />

          {/* Log Removal */}
          <LogRemovalManager authMode={authMode} mockMode={mockMode} onError={onError} />
        </div>
      </div>

      {/* ==================== CACHE OPERATIONS ==================== */}
      <div>
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-green)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.storage.cacheOperations')}
          </h3>
        </div>

        <div className="space-y-4">
          {/* Cache Clearing */}
          <CacheManager
            isAdmin={isAdmin}
            authMode={authMode}
            mockMode={mockMode}
            onError={onError}
            onSuccess={onSuccess}
          />

          {/* Corruption Detection */}
          <CorruptionManager authMode={authMode} mockMode={mockMode} onError={onError} />

          {/* Game Detection */}
          <ImageCacheContext.Provider value={imageCacheVersion}>
            <ImageInvalidateContext.Provider value={invalidateImageCache}>
              <GameCacheDetector
                mockMode={mockMode}
                isAdmin={isAdmin}
                onDataRefresh={onDataRefresh}
                refreshKey={gameCacheRefreshKey}
              />
            </ImageInvalidateContext.Provider>
          </ImageCacheContext.Provider>

          {/* Eviction Detection and Removal (outer card with two inner sub-accordions: settings + items).
            HighlightGlow scrolls to and glows this card when the user jumps here from the
            Eviction Scan schedule card. */}
          <HighlightGlow enabled={highlightEviction} scrollIntoView>
            <AccordionSection
              title={t('management.sections.data.evictedCacheData')}
              description={t('management.sections.data.evictedCacheSummary')}
              icon={Archive}
              iconColor="var(--theme-icon-orange)"
              isExpanded={evictedDataExpanded}
              onToggle={() => setEvictedDataExpanded((prev) => !prev)}
              badge={
                <div className="flex flex-wrap items-center gap-2 w-full justify-start sm:w-auto sm:justify-end">
                  {selectedEvictedCount > 0 && (
                    <Badge variant="neutral" className="badge-count">
                      {selectedEvictedCount}
                    </Badge>
                  )}
                  <SectionActionsMenu label={t('management.actions.menuLabel', 'Actions')}>
                    {(close) => (
                      <>
                        <ActionMenuItem
                          icon={
                            evictionAllExpanded ? (
                              <ChevronsDownUp className="w-3.5 h-3.5" />
                            ) : (
                              <ChevronsUpDown className="w-3.5 h-3.5" />
                            )
                          }
                          disabled={!evictedDataExpanded}
                          onClick={() => {
                            handleEvictionExpandCollapseAll();
                            close();
                          }}
                        >
                          {evictionAllExpanded
                            ? t('management.gameDetection.collapseAll')
                            : t('management.gameDetection.expandAll')}
                        </ActionMenuItem>

                        <ActionMenuItem
                          icon={<Search className="w-3.5 h-3.5" />}
                          disabled={isEvictionScanRunning || resettingEvictions}
                          onClick={() => {
                            handleStartEvictionScan();
                            close();
                          }}
                        >
                          {t('management.sections.data.runEvictionScan')}
                        </ActionMenuItem>

                        <ActionMenuDivider />

                        <ActionMenuDangerItem
                          icon={<RotateCcw className="w-3.5 h-3.5" />}
                          disabled={
                            resettingEvictions || isEvictionScanRunning || isEvictionRemovalRunning
                          }
                          onClick={() => {
                            handleResetEvictions();
                            close();
                          }}
                        >
                          {t('management.sections.data.resetEvictions')}
                        </ActionMenuDangerItem>

                        {isAdmin && (
                          <>
                            <DiskObjectActionGate
                              available={diskObjectsAvailable}
                              tooltip={t('management.capability.diskObjectsUnavailable')}
                              position="left"
                              className="block w-full"
                            >
                              <NginxReopenActionGate
                                available={selectedEvictedNginxReopenGate.available}
                                tooltip={selectedEvictedNginxReopenMessage}
                                position="left"
                                className="block w-full"
                              >
                                <ActionMenuDangerItem
                                  icon={<Trash2 className="w-3.5 h-3.5" />}
                                  disabled={
                                    selectedEvictedCount === 0 ||
                                    removeAllRunning ||
                                    removeSelectedEvictedRunning ||
                                    isEvictedRemovalRunning ||
                                    isAnyEvictedRemovalRunning ||
                                    diskActionBlocked ||
                                    checkingPermissions ||
                                    !diskObjectsAvailable ||
                                    !selectedEvictedNginxReopenGate.available
                                  }
                                  onClick={() => {
                                    setConfirmRemoveSelectedEvicted(true);
                                    close();
                                  }}
                                >
                                  {t(
                                    'management.batchSelect.removeSelectedLabel',
                                    'Remove Selected'
                                  )}
                                </ActionMenuDangerItem>
                              </NginxReopenActionGate>
                            </DiskObjectActionGate>

                            <DiskObjectActionGate
                              available={diskObjectsAvailable}
                              tooltip={t('management.capability.diskObjectsUnavailable')}
                              position="left"
                              className="block w-full"
                            >
                              <NginxReopenActionGate
                                available={allEvictedNginxReopenGate.available}
                                tooltip={allEvictedNginxReopenMessage}
                                position="left"
                                className="block w-full"
                              >
                                <ActionMenuDangerItem
                                  icon={<Trash2 className="w-3.5 h-3.5" />}
                                  disabled={
                                    evictedGames.length + evictedServices.length === 0 ||
                                    removeAllRunning ||
                                    removeSelectedEvictedRunning ||
                                    isEvictionRemovalRunning ||
                                    isAnyEvictedRemovalRunning ||
                                    diskActionBlocked ||
                                    checkingPermissions ||
                                    !diskObjectsAvailable ||
                                    !allEvictedNginxReopenGate.available
                                  }
                                  onClick={() => {
                                    setShowRemoveAllConfirm(true);
                                    close();
                                  }}
                                >
                                  {t('management.sections.data.evictionRemoveAll', 'Remove All')}
                                </ActionMenuDangerItem>
                              </NginxReopenActionGate>
                            </DiskObjectActionGate>
                          </>
                        )}
                      </>
                    )}
                  </SectionActionsMenu>
                </div>
              }
            >
              <div className="space-y-4">
                <CardDirectoryNotice notice={directoryNotice} />

                {/* Sub-accordion 1: Eviction Scan & Settings */}
                <AccordionSection
                  title={t('management.sections.data.evictionSettingsHeading')}
                  icon={Sliders}
                  iconColor="var(--theme-icon-blue)"
                  isExpanded={evictionSettingsExpanded}
                  onToggle={() => setEvictionSettingsExpanded((prev) => !prev)}
                  surface="well"
                >
                  {evictionLoading ? (
                    <LoadingState message={t('management.sections.data.evictionLoadingSettings')} />
                  ) : (
                    <div className="space-y-3">
                      <div className="space-y-2">
                        {(['show', 'showClean', 'hide', 'remove'] as const).map((mode) => (
                          <label
                            key={mode}
                            className={`eviction-mode-option p-3 rounded-lg cursor-pointer flex items-start gap-3 transition duration-150${evictionMode === mode ? ' eviction-mode-option-selected' : ''}`}
                          >
                            <input
                              type="radio"
                              name="evictionMode"
                              value={mode}
                              checked={evictionMode === mode}
                              onChange={() => setEvictionMode(mode)}
                              className="eviction-radio mt-1"
                            />
                            <div className="flex-1 min-w-0">
                              <div className="font-medium text-themed-primary">
                                {t(`management.sections.data.evictionModes.${mode}`)}
                              </div>
                              <div className="text-sm text-themed-secondary mt-1">
                                {t(`management.sections.data.evictionModes.${mode}Description`)}
                              </div>
                            </div>
                          </label>
                        ))}
                      </div>

                      {/* Prune toggle styled as the same option-card language as the modes:
                            accent left edge while enabled, description in the card body. */}
                      <label
                        className={`eviction-mode-option p-3 rounded-lg cursor-pointer flex items-start gap-3 transition duration-150${pruneOrphanedDownloads ? ' eviction-mode-option-selected' : ''}`}
                      >
                        <Checkbox
                          checked={pruneOrphanedDownloads}
                          onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                            setPruneOrphanedDownloads(e.target.checked)
                          }
                          className="mt-1"
                        />
                        <div className="flex-1 min-w-0">
                          <div className="font-medium text-themed-primary">
                            {t('management.sections.data.pruneOrphanedDownloads')}
                          </div>
                          <div className="text-sm text-themed-secondary mt-1">
                            {t('management.sections.data.pruneOrphanedDownloadsDescription')}
                          </div>
                        </div>
                      </label>

                      <div className="flex justify-end pt-3 border-t border-themed-primary">
                        <Button
                          onClick={handleSaveEviction}
                          disabled={!isEvictionDirty || evictionSaving}
                          loading={evictionSaving}
                          className="w-full sm:w-40"
                        >
                          {t('management.sections.clients.saveChanges')}
                        </Button>
                      </div>
                    </div>
                  )}
                </AccordionSection>

                {/* Sub-accordion 2: Evicted Items */}
                <AccordionSection
                  title={t('management.sections.data.evictedItemsHeading')}
                  count={
                    evictedGames.length + evictedServices.length > 0
                      ? evictedGames.length + evictedServices.length
                      : undefined
                  }
                  surface="well"
                  icon={Database}
                  iconColor="var(--theme-icon-emerald)"
                  isExpanded={evictedItemsExpanded}
                  onToggle={() => setEvictedItemsExpanded((prev) => !prev)}
                >
                  {evictedGames.length + evictedServices.length > 0 &&
                    !allEvictedNginxReopenGate.available && <ReadOnlyBadge />}
                  <EvictedItemsList
                    games={evictedGames}
                    services={evictedServices}
                    isAdmin={isAdmin}
                    datasourceConfigs={datasources}
                    onRemoveGame={handleEvictedGameRemoveClick}
                    onRemoveService={handleEvictedServiceRemoveClick}
                    diskActionBlocked={diskActionBlocked}
                    servicesSelection={evictedServicesSelectionProp}
                    gamesSelection={evictedGamesSelectionProp}
                  />
                </AccordionSection>
              </div>
            </AccordionSection>
          </HighlightGlow>
        </div>
      </div>

      {/* Eviction Remove Confirmation Modal */}
      <Modal
        opened={showRemoveConfirm}
        onClose={evictionSaving ? () => undefined : () => setShowRemoveConfirm(false)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.sections.data.evictionRemoveConfirmTitle')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.sections.data.evictionRemoveConfirmMessage')}
          </p>
          <Alert color="yellow">
            <p className="text-sm">{t('management.sections.data.evictionRemoveConfirmWarning')}</p>
          </Alert>
          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowRemoveConfirm(false)}
              disabled={evictionSaving}
            >
              {t('management.sections.data.evictionRemoveConfirmCancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={handleConfirmRemove}
              loading={evictionSaving}
            >
              {t('management.sections.data.evictionRemoveConfirmButton')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Remove All Evicted Confirmation Modal */}
      <Modal
        opened={showRemoveAllConfirm}
        onClose={() => setShowRemoveAllConfirm(false)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>
              {t(
                'management.sections.data.evictionRemoveAllConfirmTitle',
                'Remove all evicted items?'
              )}
            </span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.sections.data.evictionRemoveAllConfirmMessage', {
              count: evictedGames.length + evictedServices.length,
              defaultValue:
                'This will remove all {{count}} evicted items one at a time. Each item runs through its own removal flow (log rewrite + database cleanup) and the operation cannot be undone.'
            })}
          </p>
          <Alert color="yellow">
            <p className="text-sm">
              {t('management.sections.data.evictionRemoveAllConfirmWarning', {
                defaultValue:
                  'Only the evicted depots are removed - partially-cached games keep their on-disk files.'
              })}
            </p>
          </Alert>
          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setShowRemoveAllConfirm(false)}>
              {t('management.sections.data.evictionRemoveConfirmCancel')}
            </Button>
            <Button variant="filled" color="red" onClick={handleRemoveAllEvicted}>
              {t('management.sections.data.evictionRemoveAll', 'Remove All')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Remove Selected Evicted Confirmation Modal */}
      <ConfirmationModal
        opened={confirmRemoveSelectedEvicted}
        onClose={() => setConfirmRemoveSelectedEvicted(false)}
        onConfirm={handleRemoveSelectedEvicted}
        title={t('management.batchSelect.confirmTitle')}
        confirmLabel={t('management.batchSelect.removeSelected', { count: selectedEvictedCount })}
        confirmColor="red"
      >
        <p className="text-themed-secondary">
          {t('management.batchSelect.confirmBody', { count: selectedEvictedCount })}
        </p>
      </ConfirmationModal>

      {/* Evicted Game Removal Confirmation Modal */}
      <CacheRemovalModal
        target={evictedGameToRemove ? { type: 'game', data: evictedGameToRemove } : null}
        onClose={() => setEvictedGameToRemove(null)}
        onConfirm={confirmEvictedGameRemoval}
      />

      {/* Evicted Service Removal Confirmation Modal (fully evicted) */}
      <CacheRemovalModal
        target={evictedServiceToRemove ? { type: 'service', data: evictedServiceToRemove } : null}
        onClose={() => setEvictedServiceToRemove(null)}
        onConfirm={confirmEvictedServiceRemoval}
      />

      {/* Partial Eviction Removal Confirmation Modal */}
      {partialEvictedTarget !== null &&
        (() => {
          const isService = 'service_name' in partialEvictedTarget;
          const name = isService
            ? (partialEvictedTarget as ServiceCacheInfo).service_name
            : (partialEvictedTarget as GameCacheInfo).game_name;
          const evictedCount = partialEvictedTarget.evicted_downloads_count ?? 0;
          const evictedBytes = partialEvictedTarget.evicted_bytes ?? 0;
          const titleKey = isService
            ? 'modals.cacheRemoval.titlePartialEvictedService'
            : 'modals.cacheRemoval.titlePartialEvictedGame';
          const descKey = isService
            ? 'modals.cacheRemoval.confirmPartialEvictedService'
            : 'modals.cacheRemoval.confirmPartialEvictedGame';
          return (
            <CacheRemovalModal
              target={
                isService
                  ? { type: 'service', data: partialEvictedTarget as ServiceCacheInfo }
                  : { type: 'game', data: partialEvictedTarget as GameCacheInfo }
              }
              onClose={() => setPartialEvictedTarget(null)}
              onConfirm={confirmPartialEvictedRemoval}
              titleOverride={t(titleKey)}
              descriptionOverride={t(descKey, { name, count: evictedCount })}
              evictedCount={evictedCount}
              evictedBytes={evictedBytes}
            />
          );
        })()}
    </div>
  );
};

const StorageSection: React.FC<StorageSectionProps> = (props) => (
  <DirectoryPermissionsProvider>
    <StorageSectionContent {...props} />
  </DirectoryPermissionsProvider>
);

export default StorageSection;
