import React, { useState, useEffect, useRef } from 'react';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import {
  useNotifications,
  type UnifiedNotification,
  type NotificationStatus,
  NOTIFICATION_ANIMATION_DURATION_MS
} from '@contexts/notifications';
import themeService from '@services/theme.service';
import {
  SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY,
  MOBILE_FULL_CARD_CAP
} from '@contexts/notifications/constants';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import { APP_EVENTS } from '@utils/constants';
import { useMediaQuery } from '@hooks/useMediaQuery';
import { useScheduleDisplayModes } from '@hooks/useScheduleDisplayModes';
import { CondensedNotificationStrip } from './CondensedNotificationStrip';
import { UnifiedNotificationItem } from './UnifiedNotificationItem';
import { CANCEL_CONFIG_BY_TYPE, getNotificationColor, handleCancel } from './notificationCancel';
import { NOTIFICATION_REGISTRY } from '@contexts/notifications/notificationRegistry';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';

/**
 * The types whose registry entry computes a card id per event, so several of their cards can be
 * on screen at once reporting different entities. Derived from the registry for the same reason
 * the cancel config is: an entry that starts owning several cards must not also need a line here.
 */
const TYPES_WITH_A_CARD_PER_ENTITY = new Set<string>(
  NOTIFICATION_REGISTRY.filter((entry) => entry.getId !== undefined).map((entry) => entry.type)
);

const UniversalNotificationBar: React.FC = () => {
  const { notifications, removeNotification, updateNotification } = useNotifications();
  const [stickyDisabled, setStickyDisabled] = useState(
    themeService.getDisableStickyNotificationsSync()
  );
  const [isAnimatingOut, setIsAnimatingOut] = useState(false);
  const [shouldRender, setShouldRender] = useState(false);
  const [dismissingIds, setDismissingIds] = useState<Set<string>>(new Set());
  // Whether the condensed strip's revealed cards are currently in the bar's flow. They share
  // this bar's surface with the full cards, so the bar's bottom border and shadow must hold
  // under them even when no full card renders below the strip.
  const [stripOpen, setStripOpen] = useState(false);

  // Per-service display preference (full | condensed), live from the Schedules page. Empty until
  // seeded; an absent key resolves to full, so this drives display only and never the transport.
  const displayModes = useScheduleDisplayModes();
  // 768px anchors the established table/tile split; below it the bar caps full cards.
  const isMobile = useMediaQuery('(max-width: 767px)');
  // Hover-expand keys off pointer capability, not viewport width: a mouse-driven window between
  // the mobile boundary and a desktop breakpoint can still hover, while a large touch screen
  // cannot (its compatibility mouse events would latch a hover open with no way to unhover).
  const canHover = useMediaQuery('(hover: hover) and (pointer: fine)');
  // Tracks notifications where a deferred cancel has already been fired, so the
  // watchdog effect below never fires the same cancel twice when notifications
  // re-render. Pruned as notifications disappear.
  const deferredCancelFiredRef = useRef<Set<string>>(new Set());

  // The cancel round trip is async, so handleCancel's captured card can be stale by the time the
  // server answers. It reads the committed list through this ref instead before removing anything.
  const notificationsRef = useRef<UnifiedNotification[]>(notifications);
  useEffect(() => {
    notificationsRef.current = notifications;
  }, [notifications]);

  // Deferred-cancel watchdog: only when user clicked X before operationId existed.
  useEffect(() => {
    notifications.forEach((n) => {
      const opId = n.details?.operationId;
      // Watchdog is serverOp-only: clientQueue (bulk_removal) carries no
      // server operationId and is cancelled via the provider cascade instead.
      if (
        n.status === 'running' &&
        CANCEL_CONFIG_BY_TYPE[n.type]?.cancelKind === 'serverOp' &&
        n.details?.cancelRequested &&
        !n.details.cancelSent &&
        opId &&
        !deferredCancelFiredRef.current.has(n.id)
      ) {
        deferredCancelFiredRef.current.add(n.id);
        // Reset cancelRequested so the NEXT real click is a soft cancel, not a premature force-kill.
        updateNotification(n.id, {
          details: { ...n.details, cancelRequested: false, cancelSent: true }
        });
        // Background retry of a cancel the user already requested - the notification stays visible
        // either way, so this only needs a console trail, not a second user-facing error.
        ApiService.cancelOperation(opId).catch((err) => {
          console.error('[UniversalNotificationBar] Deferred cancel failed:', getErrorMessage(err));
        });
      }
    });

    // Prune entries whose notifications are no longer in the list so the set
    // doesn't leak across long sessions.
    const currentIds = new Set(notifications.map((n) => n.id));
    deferredCancelFiredRef.current.forEach((id) => {
      if (!currentIds.has(id)) deferredCancelFiredRef.current.delete(id);
    });
  }, [notifications, updateNotification]);

  // Listen for sticky notifications setting changes
  useEffect(() => {
    const handleStickyChange = () => {
      setStickyDisabled(themeService.getDisableStickyNotificationsSync());
    };

    window.addEventListener(APP_EVENTS.STICKY_NOTIFICATIONS_CHANGE, handleStickyChange);
    return () =>
      window.removeEventListener(APP_EVENTS.STICKY_NOTIFICATIONS_CHANGE, handleStickyChange);
  }, []);

  // Listen for notification removal events (for auto-dismiss animation)
  useEffect(() => {
    const handleNotificationRemoving = (event: CustomEvent) => {
      const notificationId = event.detail.notificationId;
      setDismissingIds((prev) => new Set(prev).add(notificationId));

      // Clean up after animation completes
      setTimeout(() => {
        setDismissingIds((prev) => {
          const newSet = new Set(prev);
          newSet.delete(notificationId);
          return newSet;
        });
      }, NOTIFICATION_ANIMATION_DURATION_MS);
    };

    window.addEventListener(
      APP_EVENTS.NOTIFICATION_REMOVING,
      handleNotificationRemoving as EventListener
    );
    return () =>
      window.removeEventListener(
        APP_EVENTS.NOTIFICATION_REMOVING,
        handleNotificationRemoving as EventListener
      );
  }, []);

  // Handle animation when notifications appear/disappear
  useEffect(() => {
    if (notifications.length > 0) {
      // Show immediately when notifications appear
      setShouldRender(true);
      setIsAnimatingOut(false);
      return;
    }
    if (!shouldRender) {
      return;
    }
    // The bar just emptied. Do NOT start the slide-out immediately: a burst can remove the last
    // notification and add a new one a fraction of a second later (one scheduled run finishing as
    // the next appears, or a run that finishes almost instantly). Starting the exit right away and
    // then reversing it when the new notification lands paints a visible dip-and-return flash on
    // the whole bar. So hold the bar fully visible for one animation beat first; a notification
    // that arrives during the hold cancels both timers with nothing ever dipped. Only a bar still
    // empty after the hold plays the slide-out, then unmounts when it finishes.
    const holdTimer = window.setTimeout(
      () => setIsAnimatingOut(true),
      NOTIFICATION_ANIMATION_DURATION_MS
    );
    const unmountTimer = window.setTimeout(() => {
      setShouldRender(false);
      setIsAnimatingOut(false);
    }, NOTIFICATION_ANIMATION_DURATION_MS * 2);

    return () => {
      window.clearTimeout(holdTimer);
      window.clearTimeout(unmountTimer);
    };
  }, [notifications.length, shouldRender]);

  // Animated dismiss handler
  const handleDismiss = (notificationId: string) => {
    // Add to dismissing set to trigger animation
    setDismissingIds((prev) => new Set(prev).add(notificationId));

    // Wait for animation to complete, then remove
    setTimeout(() => {
      removeNotification(notificationId);
      setDismissingIds((prev) => {
        const newSet = new Set(prev);
        newSet.delete(notificationId);
        return newSet;
      });
    }, NOTIFICATION_ANIMATION_DURATION_MS);
  };

  // Create cancel handler for a notification
  const getCancelHandler = (notification: UnifiedNotification) => {
    if (!(notification.type in CANCEL_CONFIG_BY_TYPE)) {
      return undefined;
    }

    return () =>
      handleCancel(notification, updateNotification, removeNotification, (id: string) =>
        notificationsRef.current.find((n) => n.id === id)
      );
  };

  // Don't render if no notifications and not animating
  if (!shouldRender) {
    return null;
  }

  const statusOrder: Partial<Record<NotificationStatus, number>> = {
    completed: 0,
    // Terminal and benign, so it sorts with the finished runs rather than dropping to the end.
    skipped: 0,
    failed: 1,
    cancelled: 1,
    running: 2,
    cancelling: 2,
    waiting: 3,
    pending: 4
  };
  // Completed/failed first, then running (comparator unchanged from the original single-list render).
  const sorted = [...notifications].sort(
    (a, b) => (statusOrder[a.status] ?? 4) - (statusOrder[b.status] ?? 4)
  );

  // Classify each notification (in the sorted order) as condensed or full. A notification
  // condenses when its service is set to condensed, OR on mobile once the full-card cap is
  // reached. Expansion never changes membership: a tap-expanded item stays in the condensed
  // group and reveals its card in place via CollapsibleRegion, so its toggle line keeps focus
  // and remains available to collapse it. The comparator above is untouched; only grouping
  // changes.
  let fullOrder = 0;
  const classified = sorted.map((notification) => {
    // Generic toasts (Run Now acknowledgments) carry their owning serviceKey in details.
    const serviceKey =
      SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY[notification.type] ??
      notification.details?.serviceKey;
    // A refused Run Now never starts a run, so it has no lifecycle notification to fold into and
    // the compact bar would answer the click with a coloured line carrying no reason. This toast is
    // the only answer that click gets, so it keeps its card whatever the service is set to. Routine
    // runs are unaffected: they never arrive as 'generic'.
    const refusedManualRun = notification.type === 'generic' && notification.status === 'skipped';
    const condensedByService =
      !refusedManualRun && serviceKey !== undefined && displayModes[serviceKey] === 'condensed';
    const orderAmongFull = condensedByService ? -1 : fullOrder++;
    const condensedByCap = isMobile && orderAmongFull >= MOBILE_FULL_CARD_CAP;
    return { notification, serviceKey, condensed: condensedByService || condensedByCap };
  });
  // One line per service in the condensed group: a manual run's acknowledgment toast and the
  // run's own lifecycle notification fold into a single disclosure instead of stacking a line
  // per notification. Notifications without a serviceKey keep a line each. Map preserves the
  // sorted order via first insertion.
  //
  // A type that owns one card per entity is the exception, and keeps a line per card: its cards
  // report DIFFERENT work under the same service, so folding them would show one of them and hide
  // the rest. A scheduled prefill running four platforms at once is four lines, not one.
  const condensedGroups = new Map<string, UnifiedNotification[]>();
  for (const item of classified) {
    if (!item.condensed) {
      continue;
    }
    const groupKey =
      item.serviceKey !== undefined && !TYPES_WITH_A_CARD_PER_ENTITY.has(item.notification.type)
        ? `svc:${item.serviceKey}`
        : `id:${item.notification.id}`;
    const group = condensedGroups.get(groupKey);
    if (group) {
      group.push(item.notification);
    } else {
      condensedGroups.set(groupKey, [item.notification]);
    }
  }
  const fullItems = classified.filter((item) => !item.condensed);

  return (
    <div className={`w-full ${!stickyDisabled ? 'sticky top-12 z-40 md:top-0 md:z-50' : ''}`}>
      <div
        className={`w-full border-b bg-[var(--theme-nav-bg)] transition-[transform,opacity] duration-300 ease-out motion-reduce:transition-none ${
          fullItems.length > 0 || stripOpen
            ? 'border-[var(--theme-nav-border)] shadow-sm'
            : 'border-transparent shadow-none'
        }`}
        style={{
          transform: isAnimatingOut ? 'translateY(-100%)' : 'translateY(0)',
          opacity: isAnimatingOut ? 0 : 1
        }}
      >
        {/* One strip spans the bar edge to edge, flush under the navigation: every condensed
            service keeps its status colour as a segment of the single line (the live run
            outranks terminal toasts for each segment's colour, fill, and pulse), and the whole
            line is one disclosure target. Rendered only when present, so the default all-full
            desktop path is the untouched full-card container below. */}
        {/* Rendered unconditionally: with zero segments the strip renders null itself, after
            fading its line out. A conditional unmount here would blink the line off in one
            frame instead. */}
        {
          <CondensedNotificationStrip
            segments={[...condensedGroups.entries()].map(([groupKey, group]) => {
              const representative =
                group.find((n) => !isTerminalNotificationStatus(n.status)) ?? group[0];
              return {
                key: groupKey,
                notification: representative,
                color: getNotificationColor(representative)
              };
            })}
            canHover={canHover}
            onOpenChange={setStripOpen}
          >
            <div className="container mx-auto px-4 pb-2">
              {/* On a phone the opened panel caps at roughly two cards and scrolls for the rest,
                  so a burst of condensed runs cannot push the page out of reach. Desktop keeps
                  the full-height panel. radius none: cards sit flush against the viewport and a
                  rounded clip would shave their corners. */}
              {isMobile ? (
                <CustomScrollbar maxHeight="12rem" paddingMode="compact" radius="none">
                  <div className="space-y-2">
                    {[...condensedGroups.values()].flat().map((notification) => (
                      <UnifiedNotificationItem
                        key={notification.id}
                        notification={notification}
                        onDismiss={() => handleDismiss(notification.id)}
                        onCancel={getCancelHandler(notification)}
                        isAnimatingOut={dismissingIds.has(notification.id)}
                      />
                    ))}
                  </div>
                </CustomScrollbar>
              ) : (
                <div className="space-y-2">
                  {[...condensedGroups.values()].flat().map((notification) => (
                    <UnifiedNotificationItem
                      key={notification.id}
                      notification={notification}
                      onDismiss={() => handleDismiss(notification.id)}
                      onCancel={getCancelHandler(notification)}
                      isAnimatingOut={dismissingIds.has(notification.id)}
                    />
                  ))}
                </div>
              )}
            </div>
          </CondensedNotificationStrip>
        }
        {fullItems.length > 0 && (
          <div className="container mx-auto px-4 py-2 space-y-2">
            {fullItems.map(({ notification }) => (
              <UnifiedNotificationItem
                key={notification.id}
                notification={notification}
                onDismiss={() => handleDismiss(notification.id)}
                onCancel={getCancelHandler(notification)}
                isAnimatingOut={dismissingIds.has(notification.id)}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  );
};

export default UniversalNotificationBar;
