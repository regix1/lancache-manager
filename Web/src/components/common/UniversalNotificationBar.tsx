import React, { useCallback, useState, useEffect, useRef } from 'react';
import {
  useNotifications,
  type UnifiedNotification,
  NOTIFICATION_ANIMATION_DURATION_MS
} from '@contexts/notifications';
import themeService from '@services/theme.service';
import ApiService from '@services/api.service';
import {
  SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY,
  MOBILE_FULL_CARD_CAP
} from '@contexts/notifications/constants';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import { APP_EVENTS } from '@utils/constants';
import i18n from '@/i18n';
import { useMediaQuery } from '@hooks/useMediaQuery';
import { useConnectionLost } from '@hooks/useConnectionLost';
import { platformDisplayModeKey, useScheduleDisplayModes } from '@hooks/useScheduleDisplayModes';
import { CondensedNotificationStrip } from './CondensedNotificationStrip';
import { BackgroundTaskControls } from './BackgroundTaskControls';
import { UnifiedNotificationItem } from './UnifiedNotificationItem';
import {
  CANCEL_CONFIG_BY_TYPE,
  getNotificationVariant,
  handleCancel,
  notifyToastError
} from './notificationCancel';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';

const UniversalNotificationBar: React.FC = () => {
  const { notifications, removeNotification, hideNotification, updateNotification } =
    useNotifications();
  const connectionLost = useConnectionLost();
  const [stickyDisabled, setStickyDisabled] = useState(
    themeService.getDisableStickyNotificationsSync()
  );
  const [isAnimatingOut, setIsAnimatingOut] = useState(false);
  const [shouldRender, setShouldRender] = useState(false);
  const [dismissingIds, setDismissingIds] = useState<Set<string>>(new Set());

  // Per-service display preference (full | condensed), live from the Schedules page, and the one
  // global default for everything a schedule does not set. This drives display only and never
  // the transport.
  const { modes, defaultMode, ready } = useScheduleDisplayModes();
  // 768px anchors the established table/tile split; below it the bar caps full cards.
  const isMobile = useMediaQuery('(max-width: 767px)');
  // Hover-expand keys off pointer capability, not viewport width: a mouse-driven window between
  // the mobile boundary and a desktop breakpoint can still hover, while a large touch screen
  // cannot (its compatibility mouse events would latch a hover open with no way to unhover).
  const canHover = useMediaQuery('(hover: hover) and (pointer: fine)');

  // The cancel and close round trips are async, so a captured card can be stale by the time the
  // server answers. They read the committed list through this ref instead before removing anything.
  const notificationsRef = useRef<UnifiedNotification[]>(notifications);
  useEffect(() => {
    notificationsRef.current = notifications;
  }, [notifications]);

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

  // Animated dismiss handler. A card that keeps an ending on the server lists the ids to close in
  // `details.closeOperationIds`; it asks the server first, so closing it here closes it on every
  // admin's screen, and only the ids the server confirmed leave with it.
  const handleDismiss = useCallback(
    (notificationId: string) => {
      const notification = notificationsRef.current.find((item) => item.id === notificationId);
      if (!notification) return;
      // A live card offers this button only while the server is unreachable. It is hidden at once,
      // not faded and removed: its run stays tracked, and a hide landing after a mid-fade row would
      // undo it.
      if (!isTerminalNotificationStatus(notification.status)) {
        hideNotification(notificationId);
        return;
      }

      const operationId = notification.details?.operationId;
      const fade = (closedOperationIds?: string[]): void => {
        // Add to dismissing set to trigger animation
        setDismissingIds((prev) => new Set(prev).add(notificationId));

        // Wait for animation to complete, then remove
        setTimeout(() => {
          const current = notificationsRef.current.find((item) => item.id === notificationId);
          if (current && current.details?.operationId === operationId) {
            removeNotification(notificationId, closedOperationIds);
          }
          setDismissingIds((prev) => {
            const newSet = new Set(prev);
            newSet.delete(notificationId);
            return newSet;
          });
        }, NOTIFICATION_ANIMATION_DURATION_MS);
      };

      const closeOperationIds = notification.details?.closeOperationIds;
      if (!closeOperationIds?.length) {
        fade();
        return;
      }
      // Each close answers 204, or 404 when another screen already closed it; both resolve.
      void Promise.allSettled(closeOperationIds.map((id) => ApiService.closeOperation(id))).then(
        (results) => {
          const closed = closeOperationIds.filter(
            (_, index) => results[index].status === 'fulfilled'
          );
          const failure = results.find(
            (result): result is PromiseRejectedResult => result.status === 'rejected'
          );
          if (failure)
            notifyToastError(i18n.t('common.notifications.closeOperationFailed'), failure.reason);
          // A kept run card the server did not close stays on screen. A bulk card leaves either
          // way: each kept run it listed that the server did not close is then drawn as its own
          // card until closed.
          if (closed.length === 0 && notification.type !== 'bulk_removal') return;
          fade(closed);
        }
      );
    },
    [removeNotification, hideNotification]
  );

  // Create cancel handler for a notification
  const getCancelHandler = useCallback(
    (notification: UnifiedNotification) =>
      handleCancel(
        notification,
        updateNotification,
        removeNotification,
        () => notificationsRef.current
      ),
    [removeNotification, updateNotification]
  );

  // Don't render if no notifications and not animating
  if (notifications.length === 0 && !shouldRender) {
    return null;
  }

  const sorted = [...notifications].sort((a, b) => {
    const startedAtComparison = a.startedAt.getTime() - b.startedAt.getTime();
    if (startedAtComparison < 0) return -1;
    if (startedAtComparison > 0) return 1;
    if (a.id === b.id) return 0;
    return a.id < b.id ? -1 : 1;
  });

  // Classify each notification (in the sorted order) as condensed or full. A notification
  // condenses when its style resolves to condensed, OR on mobile once the full-card cap is
  // reached. Expansion never changes membership: a revealed item stays in the condensed group
  // and shows its card in the strip's panel. The comparator above is untouched; only grouping
  // changes.
  let fullOrder = 0;
  const classified = sorted.flatMap((notification) => {
    const control = notification.controlOnly && !isTerminalNotificationStatus(notification.status);
    // Generic toasts carry their owning serviceKey in details.
    const serviceKey =
      SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY[notification.type] ??
      notification.details?.serviceKey;
    const platform =
      notification.type === 'scheduled_prefill' &&
      /^(Steam|Epic|Xbox|BattleNet|Riot)$/.test(notification.details?.service ?? '')
        ? notification.details?.service
        : undefined;
    // Named schedules own their style independently, even on the same persistent container.
    // Platform-only snapshots retain the platform fallback, never the outer schedule.
    const resolvedDisplayMode =
      serviceKey === undefined
        ? undefined
        : platform !== undefined
          ? (modes[
              platformDisplayModeKey(serviceKey, platform, notification.details?.scheduleId)
            ] ?? modes[platformDisplayModeKey(serviceKey, platform)])
          : modes[serviceKey];
    // A schedule's own style wins, then the one global default. [63]
    // A run card (it carries details.operationId) waits until the styles are known, so a reload
    // never draws it full and then moves it to the compact line. [62] A card the browser owns
    // (a toast, a catalog announcement, the Steam session error) never waits: its 5 s dismissal
    // starts when it is added, so holding it back could spend that time hidden, and until the
    // first settings read settles the default is still full.
    if (!ready && notification.details?.operationId !== undefined) return [];
    const displayMode = resolvedDisplayMode ?? defaultMode;
    const condensedByService = displayMode === 'condensed';
    const orderAmongFull = condensedByService || control ? -1 : fullOrder++;
    const condensedByCap = !control && isMobile && orderAmongFull >= MOBILE_FULL_CARD_CAP;
    return [
      {
        notification,
        serviceKey,
        condensed: condensedByService || condensedByCap,
        control
      }
    ];
  });
  const compactControls = classified
    .filter((item) => item.control && item.condensed)
    .map((item) => item.notification);
  const fullControls = classified
    .filter((item) => item.control && !item.condensed)
    .map((item) => item.notification);
  // One line per service in the condensed group: a popup that names a service and that
  // service's run notification fold into a single disclosure instead of stacking a line
  // per notification. Notifications without a serviceKey keep a line each. Map preserves the
  // sorted order via first insertion.
  //
  // Scheduled prefill, which owns one card per platform, is the exception and keeps a line per
  // card: its cards report DIFFERENT work under the same service, so folding them would show one
  // of them and hide the rest. A prefill running four platforms at once is four lines, not one.
  const condensedGroups = new Map<string, UnifiedNotification[]>();
  for (const item of classified) {
    if (item.control || !item.condensed) {
      continue;
    }
    const groupKey =
      item.serviceKey !== undefined && item.notification.type !== 'scheduled_prefill'
        ? `svc:${item.serviceKey}`
        : `id:${item.notification.id}`;
    const group = condensedGroups.get(groupKey);
    if (group) {
      group.push(item.notification);
    } else {
      condensedGroups.set(groupKey, [item.notification]);
    }
  }
  const fullItems = classified.filter((item) => !item.control && !item.condensed);
  const condensedSegments = [...condensedGroups.entries()].map(([groupKey, group]) => {
    const representative = group.find((n) => !isTerminalNotificationStatus(n.status)) ?? group[0];
    return {
      key: groupKey,
      notification: representative,
      variant: getNotificationVariant(representative)
    };
  });
  if (compactControls.length > 0) {
    condensedSegments.push({
      key: 'background-controls',
      notification: compactControls[0],
      variant: 'warning'
    });
  }
  const condensedPanel = (
    <div className="space-y-2">
      {compactControls.length > 0 && (
        <BackgroundTaskControls count={compactControls.length}>
          {compactControls.map((notification) => (
            <UnifiedNotificationItem
              key={notification.id}
              notification={notification}
              onDismiss={handleDismiss}
              onCancel={notification.type in CANCEL_CONFIG_BY_TYPE ? getCancelHandler : undefined}
              connectionLost={connectionLost}
            />
          ))}
        </BackgroundTaskControls>
      )}
      {[...condensedGroups.values()].flat().map((notification) => (
        <UnifiedNotificationItem
          key={notification.id}
          notification={notification}
          onDismiss={handleDismiss}
          onCancel={notification.type in CANCEL_CONFIG_BY_TYPE ? getCancelHandler : undefined}
          isAnimatingOut={dismissingIds.has(notification.id)}
          connectionLost={connectionLost}
        />
      ))}
    </div>
  );

  return (
    <div className={`w-full ${!stickyDisabled ? 'sticky top-12 z-40 md:top-0 md:z-50' : ''}`}>
      <div
        className={`w-full border-b bg-[var(--theme-nav-bg)] transition-[transform,opacity] duration-300 ease-out motion-reduce:transition-none ${
          fullItems.length > 0 || fullControls.length > 0
            ? 'border-[var(--theme-nav-border)] shadow-sm'
            : 'border-transparent shadow-none'
        }${isAnimatingOut && notifications.length === 0 ? ' -translate-y-full opacity-0' : ''}`}
      >
        {/* One strip spans the bar edge to edge, flush under the navigation: every condensed
            service keeps its status color as a segment of the single line (the live run
            outranks terminal toasts for each segment's color, fill, and pulse), and the whole
            line is one disclosure target. Rendered only when present, so the default all-full
            desktop path is the untouched full-card container below. */}
        {/* Rendered unconditionally: with zero segments the strip renders null itself, after
            fading its line out. A conditional unmount here would blink the line off in one
            frame instead. */}
        {
          <CondensedNotificationStrip segments={condensedSegments} canHover={canHover}>
            <div className="container mx-auto px-4 pb-2">
              {/* The panel floats over the page, so its height is bounded at every size and a
                  tall list scrolls inside it: about two cards on a phone, most of the viewport
                  otherwise, so every card's buttons stay reachable. radius none: cards sit flush
                  against the viewport and a rounded clip would shave their corners. */}
              <CustomScrollbar
                maxHeight={isMobile ? '12rem' : '70vh'}
                paddingMode="none"
                radius="none"
              >
                {condensedPanel}
              </CustomScrollbar>
            </div>
          </CondensedNotificationStrip>
        }
        {fullControls.length > 0 && (
          <div className="container mx-auto px-4 py-1">
            <BackgroundTaskControls count={fullControls.length}>
              {fullControls.map((notification) => (
                <UnifiedNotificationItem
                  key={notification.id}
                  notification={notification}
                  onDismiss={handleDismiss}
                  onCancel={
                    notification.type in CANCEL_CONFIG_BY_TYPE ? getCancelHandler : undefined
                  }
                  connectionLost={connectionLost}
                />
              ))}
            </BackgroundTaskControls>
          </div>
        )}
        {fullItems.length > 0 && (
          <div className="container mx-auto px-4 py-2 space-y-2">
            {fullItems.map(({ notification }) => (
              <UnifiedNotificationItem
                key={notification.id}
                notification={notification}
                onDismiss={handleDismiss}
                onCancel={notification.type in CANCEL_CONFIG_BY_TYPE ? getCancelHandler : undefined}
                isAnimatingOut={dismissingIds.has(notification.id)}
                connectionLost={connectionLost}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  );
};

export default UniversalNotificationBar;
