import ApiService from '@services/api.service';
import { getErrorMessage, isAbortError } from '@utils/error';
import { ApiError } from '@services/apiError';
import i18n from '../../i18n';
import type { UnifiedNotification } from '@contexts/notifications';
import type { BadgeVariant } from '@components/ui/Badge.types';
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
        tooltipKey: entry.cancelTooltipKey
      };
    }
  }
  return map;
})();

// ============================================================================
// Cancel Handler
// ============================================================================

/**
 * Surface a genuine failure of a card's own request (a cancel, a force kill, a close) to the user
 * via the `show-toast` bridge. `handleCancel` is a module-level helper (not a hook/component), so
 * `useErrorHandler` is unavailable here - this mirrors it using the documented non-hook escape
 * hatch, which NotificationsContext bridges into the same generic notification the hook would
 * create. The bar's close request raises the same toast, so a failed close reads like a failed
 * cancel.
 */
export const notifyToastError = (message: string, error: unknown): void => {
  window.dispatchEvent(
    new CustomEvent(APP_EVENTS.SHOW_TOAST, {
      detail: { type: 'error', message, error: getErrorMessage(error) }
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
  getNotifications: () => UnifiedNotification[]
): Promise<void> => {
  if (isTerminalNotificationStatus(notification.status)) return;
  const cancelKind = CANCEL_CONFIG_BY_TYPE[notification.type]?.cancelKind ?? 'none';
  if (cancelKind === 'none') return;
  if (cancelKind === 'clientQueue') {
    updateNotification(notification.id, (current) =>
      isTerminalNotificationStatus(current.status)
        ? {}
        : {
            details: { ...current.details, cancelRequested: true, cancelling: true }
          }
    );
    return;
  }
  // A card without an operation id shows no X (UnifiedNotificationItem), and every run card
  // carries its id from its first row.
  const operationId = notification.details?.operationId;
  if (!operationId) return;
  // The card this request belongs to when the answer lands. A run card matches through every
  // operation id merged into it: a promotion onto a run that was already going merges the card
  // into one with the OLDER card id while the request is in flight, so neither the clicked card's
  // id nor its current operation id finds it then. A card the browser owns (a sign-in) has no
  // merged ids and matches by its own id.
  const findLive = (): UnifiedNotification | undefined =>
    getNotifications().find((n) =>
      n.details?.operationIds
        ? n.details.operationIds.includes(operationId)
        : n.id === notification.id
    );
  if (pendingCancels.has(operationId) || findLive()?.details?.cancelPending) return;
  const force = notification.details?.cancelRequested === true;
  pendingCancels.add(operationId);
  updateNotification(notification.id, (current) =>
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
    const live = findLive();
    if (!live || isTerminalNotificationStatus(live.status)) return;
    // The answer describes the operation the cancel actually reached, so `alreadyFinished` means
    // the work ended even when the request named the run this card was promoted from.
    if (result && 'alreadyFinished' in result && result.alreadyFinished === true) {
      removeNotification(live.id);
    } else {
      updateNotification(live.id, (current) =>
        !isTerminalNotificationStatus(current.status)
          ? {
              status: 'cancelling',
              details: { ...current.details, cancelPending: false, cancelling: true }
            }
          : {}
      );
    }
  } catch (err: unknown) {
    const live = findLive();
    if (!live || isTerminalNotificationStatus(live.status)) return;
    if (isAbortError(err)) {
      updateNotification(live.id, (current) =>
        !isTerminalNotificationStatus(current.status)
          ? { details: { ...current.details, cancelPending: false } }
          : {}
      );
      return;
    }
    console.error('Cancellation failed:', { operationId, force, error: err });
    if (err instanceof ApiError && err.status === 404) {
      removeNotification(live.id);
      return;
    }
    notifyToastError(
      i18n.t(
        force
          ? 'common.notifications.forceKillOperationFailed'
          : 'common.notifications.cancelOperationFailed'
      ),
      err
    );
    updateNotification(live.id, (current) =>
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
  } finally {
    pendingCancels.delete(operationId);
  }
};

// ============================================================================
// Notification Helper Functions
// ============================================================================

/**
 * The status variant a card and its compact strip segment are drawn in, via the shared status
 * vocabulary so a card and a badge never disagree about the same word. Each variant's color is
 * written once, in the `notification-status--*` rules (styles/utilities/animations.css).
 */
export const getNotificationVariant = (notification: UnifiedNotification): BadgeVariant => {
  if (notification.details?.cancelled) {
    return VARIANT_BY_STATUS['cancelled'];
  }

  // A card that carries its real semantic in details.notificationType reads it before its status:
  // the show-toast bridge stores every toast but an error with status 'completed', and a run that
  // succeeded with a warning ends 'completed' too, so a warning toast and that run must read amber,
  // never completed-green.
  if (notification.details?.notificationType) {
    return notification.details.notificationType;
  }

  // `skipped` is amber because the run did nothing, so it is neither the green of a finished
  // run nor the red of a broken one, and warning already has a glow tone in the condensed strip.
  // `pending` and `cancelling` carry no row of their own: both are still in flight, so they
  // read the way a running run does.
  return VARIANT_BY_STATUS[notification.status] ?? 'info';
};
