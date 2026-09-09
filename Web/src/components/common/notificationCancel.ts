import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import { ApiError } from '@services/apiError';
import i18n from '../../i18n';
import type { UnifiedNotification } from '@contexts/notifications';
import { VARIANT_BY_STATUS } from '@utils/statusVariant';
import { NOTIFICATION_REGISTRY } from '@contexts/notifications/notificationRegistry';
import type { CancelKind, NotificationsContextType } from '@contexts/notifications/types';
import { APP_EVENTS } from '@utils/constants';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';

// ============================================================================
// Cancellable Operation Types (derived from the registry — single source)
// ============================================================================

interface CancelConfig {
  cancelKind: CancelKind;
  tooltipKey: string;
  allowsDeferredCancel: boolean;
}

/**
 * Per-type cancel config derived from NOTIFICATION_REGISTRY (every entry with
 * cancelKind !== 'none' that carries a tooltip key). This includes the
 * client-only `bulk_removal` type, whose metadata-only registry entry
 * (cancelKind 'clientQueue') makes the X button flip a flag the always-mounted
 * BulkRemovalProvider's cascade effect observes — so the registry loop is the
 * single source for cancel wiring.
 */
export const CANCEL_CONFIG_BY_TYPE: Record<string, CancelConfig> = (() => {
  const map: Record<string, CancelConfig> = {};
  for (const entry of NOTIFICATION_REGISTRY) {
    if (entry.cancelKind !== 'none' && entry.cancelTooltipKey) {
      map[entry.type] = {
        cancelKind: entry.cancelKind,
        tooltipKey: entry.cancelTooltipKey,
        allowsDeferredCancel: entry.allowsDeferredCancel === true
      };
    }
  }
  return map;
})();

// ============================================================================
// Cancel Handler
// ============================================================================

/**
 * Surface a genuine cancel/force-kill failure to the user via the `show-toast` bridge. `handleCancel`
 * is a module-level helper (not a hook/component), so `useErrorHandler` is unavailable here - this
 * mirrors it using the documented non-hook escape hatch, which NotificationsContext bridges into the
 * same generic notification the hook would create.
 */
const notifyToastError = (i18nKey: string): void => {
  window.dispatchEvent(
    new CustomEvent(APP_EVENTS.SHOW_TOAST, {
      detail: { type: 'error', message: i18n.t(i18nKey) }
    })
  );
};

/**
 * Every write below patches `details` from the card as it stands when the write runs, not from the
 * card captured at click time. The cancel round trip is async and `updateNotification` merges at the
 * top level, so a value-shaped patch built from the click-time card replaces the whole `details`
 * object and erases anything the terminal handler wrote while the request was in flight - including
 * the `cancelled: true` that makes the card read as canceled and draw gray.
 */
const pendingCancels = new Set<string>();

export const handleCancel = async (
  notification: UnifiedNotification,
  updateNotification: NotificationsContextType['updateNotification'],
  removeNotification: (id: string) => void,
  getLiveNotification: (id: string) => UnifiedNotification | undefined,
  deferred = false
): Promise<boolean> => {
  if (isTerminalNotificationStatus(notification.status)) return false;
  const cancelKind = CANCEL_CONFIG_BY_TYPE[notification.type]?.cancelKind ?? 'none';
  if (cancelKind === 'none') return false;
  if (cancelKind === 'clientQueue') {
    updateNotification(notification.id, (current) =>
      isTerminalNotificationStatus(current.status)
        ? {}
        : {
            details: { ...current.details, cancelRequested: true, cancelling: true }
          }
    );
    return true;
  }
  const operationId = notification.details?.operationId;
  if (!operationId) {
    updateNotification(notification.id, (current) =>
      isTerminalNotificationStatus(current.status)
        ? {}
        : {
            details: { ...current.details, cancelRequested: true }
          }
    );
    return true;
  }
  if (
    pendingCancels.has(operationId) ||
    getLiveNotification(notification.id)?.details?.cancelPending
  )
    return false;
  const force = !deferred && notification.details?.cancelRequested === true;
  pendingCancels.add(operationId);
  updateNotification(notification.id, (current) =>
    current.details?.operationId === operationId &&
    current.instanceVersion === notification.instanceVersion &&
    !isTerminalNotificationStatus(current.status)
      ? {
          details: {
            ...current.details,
            cancelRequested: true,
            cancelSent: true,
            cancelPending: true
          }
        }
      : {}
  );
  try {
    const result = force
      ? await ApiService.forceKillOperation(operationId)
      : await ApiService.cancelOperation(operationId);
    const live = getLiveNotification(notification.id);
    if (
      !live ||
      live.details?.operationId !== operationId ||
      live.instanceVersion !== notification.instanceVersion ||
      isTerminalNotificationStatus(live.status)
    )
      return true;
    if (result && 'alreadyFinished' in result && result.alreadyFinished === true) {
      removeNotification(notification.id);
    } else {
      updateNotification(notification.id, (current) =>
        current.details?.operationId === operationId &&
        current.instanceVersion === notification.instanceVersion &&
        !isTerminalNotificationStatus(current.status)
          ? {
              status: 'cancelling',
              details: { ...current.details, cancelPending: false, cancelling: true }
            }
          : {}
      );
    }
    return true;
  } catch (err: unknown) {
    if (isAbortError(err)) {
      updateNotification(notification.id, (current) =>
        current.details?.operationId === operationId &&
        current.instanceVersion === notification.instanceVersion &&
        !isTerminalNotificationStatus(current.status)
          ? { details: { ...current.details, cancelPending: false } }
          : {}
      );
      return false;
    }
    const live = getLiveNotification(notification.id);
    if (
      !live ||
      live.details?.operationId !== operationId ||
      live.instanceVersion !== notification.instanceVersion ||
      isTerminalNotificationStatus(live.status)
    )
      return true;
    console.error('Cancellation failed:', { operationId, force, error: err });
    if (err instanceof ApiError && err.status === 404) {
      removeNotification(notification.id);
      return true;
    }
    notifyToastError(
      force
        ? 'common.notifications.forceKillOperationFailed'
        : 'common.notifications.cancelOperationFailed'
    );
    updateNotification(notification.id, (current) =>
      current.details?.operationId === operationId &&
      current.instanceVersion === notification.instanceVersion &&
      !isTerminalNotificationStatus(current.status)
        ? {
            details: {
              ...current.details,
              cancelPending: false,
              cancelRequested: false,
              cancelSent: false,
              cancelling: false
            }
          }
        : {}
    );
    return false;
  } finally {
    pendingCancels.delete(operationId);
  }
};

// ============================================================================
// Notification Helper Functions
// ============================================================================

/**
 * The badge variant a status resolves to, drawn as a solid line colour. `neutral` has no
 * status token of its own and borrows the grey the neutral badge fill is built from
 * (`badges.css:93`), so grey means the same thing on a card as it does on a pill.
 */
const STATUS_COLOR_BY_VARIANT: Record<string, string> = {
  success: 'var(--theme-success)',
  error: 'var(--theme-error)',
  warning: 'var(--theme-warning)',
  info: 'var(--theme-info)',
  waiting: 'var(--theme-waiting)',
  neutral: 'var(--theme-text-secondary)'
};

/**
 * Gets the status color for a notification based on its current state, via the shared
 * status vocabulary so a card and a badge never disagree about the same word.
 */
export const getNotificationColor = (notification: UnifiedNotification): string => {
  if (notification.details?.cancelled) {
    return STATUS_COLOR_BY_VARIANT[VARIANT_BY_STATUS['cancelled']];
  }

  // Toast-style notifications carry their real semantic in details.notificationType
  // (the show-toast bridge stores every toast with status 'completed'), so color by
  // that semantic first - an error toast must read red, never completed-green.
  // Scoped to generic so operation cards keep pure status semantics.
  if (notification.type === 'generic' && notification.details?.notificationType) {
    const typeColorMap: Record<string, string> = {
      success: 'var(--theme-success)',
      error: 'var(--theme-error)',
      warning: 'var(--theme-warning)',
      info: 'var(--theme-info)'
    };
    const typeColor = typeColorMap[notification.details.notificationType];
    if (typeColor) {
      return typeColor;
    }
  }

  // `skipped` is amber because the run did nothing, so it is neither the green of a finished
  // run nor the red of a broken one, and warning already has a glow tone in the condensed strip.
  // `pending` and `cancelling` carry no row of their own: both are still in flight, so they
  // read the way a running run does.
  return STATUS_COLOR_BY_VARIANT[VARIANT_BY_STATUS[notification.status] ?? 'info'];
};
