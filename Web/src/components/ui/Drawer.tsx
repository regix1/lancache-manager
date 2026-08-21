import React, { useEffect, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { createPortal } from 'react-dom';

/** Share of the panel's own width a swipe has to clear to dismiss it; below that it springs back. */
const SWIPE_CLOSE_FRACTION = 0.3;
/** Travel before the gesture commits to being a swipe rather than the body scrolling. */
const SWIPE_AXIS_LOCK_PX = 10;

interface SwipeStart {
  x: number;
  y: number;
  /** Panel width when the finger landed, so the dismiss threshold scales with the viewport. */
  width: number;
}

interface DrawerProps {
  opened: boolean;
  onClose: () => void;
  position?: 'left' | 'right';
  title?: React.ReactNode;
  children: React.ReactNode;
  classNames?: {
    header?: string;
    body?: string;
    content?: string;
    title?: string;
  };
}

const Drawer: React.FC<DrawerProps> = ({
  opened,
  onClose,
  position = 'right',
  title,
  children,
  classNames
}) => {
  const { t } = useTranslation();
  const panelRef = useRef<HTMLDivElement | null>(null);
  const swipeStart = useRef<SwipeStart | null>(null);
  const swipeTravel = useRef<number>(0);
  const swipeClaimed = useRef<boolean>(false);

  // A right panel leaves to the right and a left panel to the left; folding that into one sign
  // keeps the rest of the gesture math direction-agnostic.
  const dismissSign = position === 'left' ? -1 : 1;

  const handleKeyDown = useCallback(
    (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    },
    [onClose]
  );

  useEffect(() => {
    if (opened) {
      document.addEventListener('keydown', handleKeyDown);
      document.documentElement.classList.add('modal-open');
    }
    return () => {
      document.removeEventListener('keydown', handleKeyDown);
      document.documentElement.classList.remove('modal-open');
    };
  }, [opened, handleKeyDown]);

  const setSwipeOffset = (travel: number): void => {
    panelRef.current?.style.setProperty('--drawer-swipe-x', `${travel * dismissSign}px`);
  };

  const handleTouchStart = (e: React.TouchEvent<HTMLDivElement>): void => {
    const touch = e.touches[0];
    const panel = panelRef.current;
    if (!touch || !panel) return;
    swipeStart.current = {
      x: touch.clientX,
      y: touch.clientY,
      width: panel.getBoundingClientRect().width
    };
    swipeTravel.current = 0;
  };

  const handleTouchMove = (e: React.TouchEvent<HTMLDivElement>): void => {
    const start = swipeStart.current;
    const touch = e.touches[0];
    if (!start || !touch) return;

    const travel = (touch.clientX - start.x) * dismissSign;
    const drift = Math.abs(touch.clientY - start.y);

    if (!swipeClaimed.current) {
      // Inside the deadzone the direction is still noise, so keep waiting.
      if (travel < SWIPE_AXIS_LOCK_PX && drift < SWIPE_AXIS_LOCK_PX) return;
      // Mostly vertical is the body scrolling, and a pull away from the edge is not a dismiss;
      // either way this touch is not ours, so stop watching it.
      if (travel < SWIPE_AXIS_LOCK_PX || drift > travel) {
        swipeStart.current = null;
        return;
      }
      swipeClaimed.current = true;
      // Retires the open animation for good. It must never be restored: re-enabling it restarts
      // drawer-slide-in from translateX(100%), which replays the whole open the moment the finger
      // lifts. It also stops a swipe begun inside the 0.2s open window from being overridden.
      panelRef.current?.classList.add('custom-drawer-panel--swiped');
    }

    // Dragging back past the edge would open a gap beside the panel.
    swipeTravel.current = Math.max(0, travel);
    setSwipeOffset(swipeTravel.current);
  };

  const handleTouchEnd = (): void => {
    const start = swipeStart.current;
    swipeStart.current = null;
    if (!start || !swipeClaimed.current) return;
    swipeClaimed.current = false;

    if (swipeTravel.current >= start.width * SWIPE_CLOSE_FRACTION) {
      onClose();
      return;
    }
    setSwipeOffset(0);
  };

  // The OS can take the touch back mid-drag (incoming call, system edge gesture), which otherwise
  // leaves the panel parked half off-screen with no finger to finish the swipe.
  const handleTouchCancel = (): void => {
    swipeStart.current = null;
    if (!swipeClaimed.current) return;
    swipeClaimed.current = false;
    setSwipeOffset(0);
  };

  if (!opened) return null;

  return createPortal(
    <div className="custom-drawer-root">
      <div className="custom-drawer-overlay" onClick={onClose} />
      <div
        ref={panelRef}
        className={`custom-drawer-panel custom-drawer-${position} ${classNames?.content ?? ''}`}
        role="dialog"
        aria-modal="true"
        onTouchStart={handleTouchStart}
        onTouchMove={handleTouchMove}
        onTouchEnd={handleTouchEnd}
        onTouchCancel={handleTouchCancel}
      >
        <div className={`custom-drawer-header ${classNames?.header ?? ''}`}>
          <h2 className={`custom-drawer-title ${classNames?.title ?? ''}`}>{title}</h2>
          <button
            className="btn-icon-square btn-icon-square--sm custom-drawer-close"
            onClick={onClose}
            aria-label={t('common.close')}
            type="button"
          >
            <svg
              width="16"
              height="16"
              viewBox="0 0 16 16"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
            >
              <line x1="3" y1="3" x2="13" y2="13" />
              <line x1="13" y1="3" x2="3" y2="13" />
            </svg>
          </button>
        </div>
        <div className={`custom-drawer-body ${classNames?.body ?? ''}`}>{children}</div>
      </div>
    </div>,
    document.body
  );
};

export default Drawer;
