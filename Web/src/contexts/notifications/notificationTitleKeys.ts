import type { NotificationType } from './types';

/** Stable operation eyebrow keys for the Universal Notification surface. */
export const NOTIFICATION_TITLE_KEYS: Record<NotificationType, string | null> = {
  log_processing: 'common.notifications.titles.logProcessing',
  cache_clearing: 'common.notifications.titles.cacheClearing',
  log_removal: 'common.notifications.titles.logRemoval',
  service_removal: 'common.notifications.titles.serviceRemoval',
  game_removal: 'common.notifications.titles.gameRemoval',
  corruption_removal: 'common.notifications.titles.corruptionRemoval',
  corruption_detection: 'common.notifications.titles.corruptionDetection',
  database_reset: 'common.notifications.titles.databaseReset',
  depot_mapping: 'common.notifications.titles.depotMapping',
  game_detection: 'common.notifications.titles.gameDetection',
  data_import: 'common.notifications.titles.dataImport',
  epic_game_mapping: 'common.notifications.titles.epicGameMapping',
  xbox_game_mapping: 'common.notifications.titles.xboxGameMapping',
  eviction_scan: 'common.notifications.titles.evictionScan',
  eviction_removal: 'common.notifications.titles.evictionRemoval',
  cache_size_scan: 'common.notifications.titles.cacheSizeScan',
  scheduled_prefill: 'common.notifications.titles.scheduledPrefill',
  log_rotation: 'common.notifications.titles.logRotation',
  game_image_fetch: 'common.notifications.titles.gameImageFetch',
  cache_snapshot: 'common.notifications.titles.cacheSnapshot',
  operation_history_cleanup: 'common.notifications.titles.operationHistoryCleanup',
  performance_optimization: 'common.notifications.titles.performanceOptimization',
  dashboard_cache_warmer: 'common.notifications.titles.dashboardCacheWarmer',
  bulk_removal: 'common.notifications.titles.bulkRemoval',
  generic: null
};
