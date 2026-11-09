import { SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY } from './constants';
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
  depot_mapping: 'management.schedules.services.depotMapping.displayName',
  game_detection: 'management.schedules.services.gameDetection.displayName',
  data_import: 'common.notifications.titles.dataImport',
  epic_game_mapping: 'management.schedules.services.epicMapping.displayName',
  epic_catalog_update: 'management.schedules.services.epicMapping.displayName',
  xbox_game_mapping: 'management.schedules.services.xboxMapping.displayName',
  xbox_catalog_update: 'management.schedules.services.xboxMapping.displayName',
  battle_net_game_mapping: 'common.notifications.titles.battleNetGameMapping',
  riot_game_mapping: 'common.notifications.titles.riotGameMapping',
  eviction_scan: 'management.schedules.services.cacheReconciliation.displayName',
  eviction_removal: 'common.notifications.titles.evictionRemoval',
  cache_size_scan: 'management.schedules.services.cacheSizeScan.displayName',
  scheduled_prefill: 'management.schedules.services.scheduledPrefill.displayName',
  log_rotation: 'management.schedules.services.logRotation.displayName',
  game_image_fetch: 'management.schedules.services.gameImageFetch.displayName',
  cache_snapshot: 'management.schedules.services.cacheSnapshot.displayName',
  operation_history_cleanup: 'management.schedules.services.operationHistoryCleanup.displayName',
  dashboard_cache_warmer: 'management.schedules.services.dashboardCacheWarmer.displayName',
  bulk_removal: 'common.notifications.titles.bulkRemoval',
  prefill_login: 'common.notifications.titles.prefillLogin',
  download_history_upgrade: 'common.notifications.titles.downloadHistoryUpgrade',
  unmapped_file_removal: 'common.notifications.titles.unmappedFileRemoval',
  cache_repair: 'common.notifications.titles.cacheRepair',
  // A one-shot error toast rather than an operation, so it carries no operation eyebrow.
  steam_session_error: null,
  generic: null
};

/** The card title key of a schedule, by its schedule key; null for a key no schedule uses. */
export const scheduleTitleKey = (scheduleKey: string): string | null => {
  const entry = Object.entries(SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY).find(
    ([, serviceKey]) => serviceKey === scheduleKey
  );
  return entry ? NOTIFICATION_TITLE_KEYS[entry[0] as NotificationType] : null;
};

/** Titles of operations that draw no card but can still hold a run back, by operation wire type. */
export const CARDLESS_OPERATION_TITLE_KEYS: Record<string, string> = {
  statusCheck: 'common.notifications.titles.statusCheck',
  cacheFileCount: 'common.notifications.titles.cacheFileCount'
};
