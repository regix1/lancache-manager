import React, { useState, useRef, useEffect, useLayoutEffect } from 'react';
import { createPortal } from 'react-dom';
import { Info } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useMediaQuery } from '@hooks/useMediaQuery';
import { APP_EVENTS } from '@utils/constants';
import { clampToViewport } from '@utils/viewportClamp';

type TooltipPosition = 'top' | 'bottom' | 'left' | 'right';
type TooltipStrategy = 'edge' | 'overlay';

interface TooltipProps {
  children?: React.ReactNode;
  content: React.ReactNode;
  position?: TooltipPosition;
  offset?: number;
  className?: string;
  contentClassName?: string;
  strategy?: TooltipStrategy;
  style?: React.CSSProperties;
}

const DEFAULT_OFFSET = 8;

// Long enough to cross the gap between the trigger and the box, so the pointer can reach the text
// instead of the box vanishing on the way. Required for hover content: someone magnifying the screen
// has to be able to pan across it.
const HIDE_GRACE_MS = 150;

// True when the trigger, or anything inside it, is painting less text than it holds. Both axes
// count, because the ellipsis comes from `truncate` on the width or `line-clamp` on the height, and
// only elements that actually clip are asked: one with visible overflow spills rather than hides, so
// its scroll size exceeding its client size hides nothing from the reader. Measured per hover rather
// than cached, which keeps it honest across resizes and font swaps.
const hasClippedText = (root: HTMLElement | null): boolean => {
  if (!root) {
    return false;
  }
  const nodes: HTMLElement[] = [root, ...root.querySelectorAll<HTMLElement>('*')];
  return nodes.some((node) => {
    const { overflowX, overflowY } = window.getComputedStyle(node);
    return (
      (overflowX !== 'visible' && node.scrollWidth > node.clientWidth + 1) ||
      (overflowY !== 'visible' && node.scrollHeight > node.clientHeight + 1)
    );
  });
};

const normalizeText = (value: string): string => value.replace(/\s+/g, ' ').trim();

// A box repeating words the reader can already read adds nothing, and lands on top of the very text
// it copies. So a plain-string content that matches the trigger's own visible text earns its box
// only while that text is actually cut off. Content saying anything the trigger does not - a full
// timestamp behind a relative one, the reason a control is disabled - is never suppressed, because
// the strings differ. Nothing at the call site has to opt in, and nothing written later has to
// remember to.
const repeatsVisibleText = (trigger: HTMLElement | null, content: React.ReactNode): boolean => {
  if (typeof content !== 'string' || trigger === null) {
    return false;
  }
  const visible = normalizeText(trigger.textContent ?? '');
  return visible !== '' && visible === normalizeText(content);
};

export const Tooltip: React.FC<TooltipProps> = ({
  children,
  content,
  position = 'top',
  offset = DEFAULT_OFFSET,
  className,
  contentClassName = '',
  strategy = 'edge',
  style
}) => {
  const [show, setShow] = useState(false);
  const [x, setX] = useState(0);
  const [y, setY] = useState(0);
  const isMobile = useMediaQuery('(max-width: 767px)');
  const [globallyDisabled, setGloballyDisabled] = useState(
    document.documentElement.getAttribute('data-disable-tooltips') === 'true'
  );
  const triggerRef = useRef<HTMLDivElement>(null);
  const showTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const hideTimeoutRef = useRef<NodeJS.Timeout | null>(null);

  // Listen for tooltips setting changes
  useEffect(() => {
    const handleTooltipsChange = () => {
      const disabled = document.documentElement.getAttribute('data-disable-tooltips') === 'true';
      setGloballyDisabled(disabled);
      if (disabled) {
        setShow(false);
      }
    };
    window.addEventListener(APP_EVENTS.TOOLTIPS_CHANGE, handleTooltipsChange);
    return () => window.removeEventListener(APP_EVENTS.TOOLTIPS_CHANGE, handleTooltipsChange);
  }, []);

  // Only disable if globally disabled (not on mobile - we use touch there)
  const tooltipsDisabled = globallyDisabled;

  // Add scroll listener and click-outside handler to hide tooltip
  useEffect(() => {
    if (!show) return;

    const handleScroll = () => {
      setShow(false);
    };

    // Close tooltip when clicking outside (for mobile tap-to-toggle)
    const handleClickOutside = (e: MouseEvent | TouchEvent) => {
      if (triggerRef.current && !triggerRef.current.contains(e.target as Node)) {
        setShow(false);
      }
    };

    // Escape closes it without moving the pointer or the focus, which hover content has to allow.
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setShow(false);
      }
    };

    // Listen for scroll events on window and any scrollable parents
    window.addEventListener('scroll', handleScroll, { passive: true, capture: true });
    window.addEventListener('touchmove', handleScroll, { passive: true, capture: true });
    document.addEventListener('keydown', handleKeyDown, { capture: true });

    // Add click-outside listener with a small delay to avoid immediate close
    const timeoutId = setTimeout(() => {
      document.addEventListener('click', handleClickOutside, { capture: true });
      document.addEventListener('touchstart', handleClickOutside, { capture: true });
    }, 10);

    return () => {
      clearTimeout(timeoutId);
      window.removeEventListener('scroll', handleScroll, { capture: true });
      window.removeEventListener('touchmove', handleScroll, { capture: true });
      document.removeEventListener('keydown', handleKeyDown, { capture: true });
      document.removeEventListener('click', handleClickOutside, { capture: true });
      document.removeEventListener('touchstart', handleClickOutside, { capture: true });
    };
  }, [show]);

  // Default children with conditional cursor style
  const defaultChildren = (
    <Info
      className={`w-5 h-5 text-themed-muted p-1.5 -m-1.5 ${tooltipsDisabled ? '' : 'cursor-help'}`}
    />
  );
  const childContent = children ?? defaultChildren;

  // A tooltip with no content must not render an empty hover box. Callers commonly pass
  // content conditionally (e.g. only when a control is disabled/read-only), so guard on it.
  const hasContent = content != null && content !== '';

  // Cleanup timeouts on unmount
  useEffect(() => {
    return () => {
      if (showTimeoutRef.current) clearTimeout(showTimeoutRef.current);
      if (hideTimeoutRef.current) clearTimeout(hideTimeoutRef.current);
    };
  }, []);

  // Everything this box would say is already on the screen, unclipped.
  const addsNothing = (): boolean =>
    repeatsVisibleText(triggerRef.current, content) && !hasClippedText(triggerRef.current);

  const handleMouseEnter = (e: React.MouseEvent) => {
    if (tooltipsDisabled) return;

    // Clear any pending hide
    if (hideTimeoutRef.current) {
      clearTimeout(hideTimeoutRef.current);
      hideTimeoutRef.current = null;
    }

    if (addsNothing()) return;

    setX(e.clientX);
    setY(e.clientY);

    // Small delay before showing (150ms)
    showTimeoutRef.current = setTimeout(() => {
      setShow(true);
    }, 150);
  };

  // Keyboard reaches the same content, without which a tooltip is mouse-only and the full text
  // behind an ellipsis is unreachable for anyone tabbing. Focus lands on an interactive trigger, so
  // it anchors to the element's own box rather than to a pointer position, and shows without the
  // hover delay because arriving by Tab is already deliberate.
  const handleFocus = (event: React.FocusEvent) => {
    if (tooltipsDisabled || addsNothing()) return;

    // Keyboard arrivals only. A mouse press focuses the control too, and `focusin` beats the click,
    // so showing on every focus put the box on screen during the press and the click handler below
    // took it away again: a blink on every click of a control that has a tooltip. `:focus-visible`
    // is the browser's own answer to which arrivals deserve a focus affordance.
    if (!(event.target as HTMLElement).matches(':focus-visible')) return;

    const rect = triggerRef.current?.getBoundingClientRect();
    if (rect) {
      setX(rect.left + rect.width / 2);
      setY(rect.top);
    }
    setShow(true);
  };

  const handleBlur = () => {
    if (showTimeoutRef.current) {
      clearTimeout(showTimeoutRef.current);
      showTimeoutRef.current = null;
    }
    setShow(false);
  };

  // Entering the box itself cancels the pending hide, so the pointer can rest on the text.
  const handleTooltipEnter = () => {
    if (hideTimeoutRef.current) {
      clearTimeout(hideTimeoutRef.current);
      hideTimeoutRef.current = null;
    }
  };

  const handleMouseMove = (e: React.MouseEvent) => {
    if (!tooltipsDisabled && strategy === 'overlay') {
      setX(e.clientX);
      setY(e.clientY);
    }
  };

  const handleMouseLeave = () => {
    // Clear any pending show
    if (showTimeoutRef.current) {
      clearTimeout(showTimeoutRef.current);
      showTimeoutRef.current = null;
    }

    // Hide after a grace period rather than at once, so the pointer can travel the gap into the box.
    hideTimeoutRef.current = setTimeout(() => {
      setShow(false);
    }, HIDE_GRACE_MS);
  };

  return (
    <>
      <div
        ref={triggerRef}
        className={className || 'inline-flex'}
        style={style}
        onMouseEnter={handleMouseEnter}
        onMouseMove={handleMouseMove}
        onMouseLeave={handleMouseLeave}
        onFocus={handleFocus}
        onBlur={handleBlur}
        onClick={(e) => {
          // On mobile, toggle tooltip on tap. Do NOT stopPropagation: the tap must
          // still bubble to a clickable parent (e.g. a Downloads card whose onClick
          // opens the details Drawer). Keep preventDefault to cancel the wrapped
          // element's default action without blocking the parent's click handler.
          if (isMobile && !tooltipsDisabled) {
            if (addsNothing()) {
              return;
            }
            e.preventDefault();
            if (!show) {
              const rect = triggerRef.current?.getBoundingClientRect();
              if (rect) {
                setX(rect.left + rect.width / 2);
                setY(rect.top);
              }
            }
            setShow(!show);
          } else {
            // On desktop, hide tooltip on click (user is taking action)
            setShow(false);
          }
        }}
      >
        {childContent}
      </div>

      {show &&
        hasContent &&
        !tooltipsDisabled &&
        strategy === 'overlay' &&
        createPortal(<OverlayTooltip x={x} y={y} content={content} />, document.body)}

      {show &&
        hasContent &&
        !tooltipsDisabled &&
        strategy === 'edge' &&
        triggerRef.current &&
        createPortal(
          <EdgeTooltip
            trigger={triggerRef.current}
            content={content}
            position={position}
            offset={offset}
            contentClassName={contentClassName}
            onMouseEnter={handleTooltipEnter}
            onMouseLeave={handleMouseLeave}
          />,
          document.body
        )}
    </>
  );
};

// Cursor/tap-anchored tooltips for the 'overlay' strategy. Positioned relative to the
// pointer, then flipped/clamped to the viewport so they don't run off-screen near
// edges (a raw x+10/y+10 offset with no clamping was the cause of tooltips and
// popovers being cut off near the right/bottom edge, especially on mobile taps).
const OverlayTooltip: React.FC<{
  x: number;
  y: number;
  content: React.ReactNode;
}> = ({ x, y, content }) => {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null);
  const [isReady, setIsReady] = useState(false);

  useLayoutEffect(() => {
    if (!ref.current) return;

    const rect = ref.current.getBoundingClientRect();
    const viewportPadding = 12;
    const offset = 10;

    let left = x + offset;
    let top = y + offset;

    // Flip to the other side of the cursor if the default placement would overflow
    if (left + rect.width > window.innerWidth - viewportPadding) {
      left = x - rect.width - offset;
    }
    if (top + rect.height > window.innerHeight - viewportPadding) {
      top = y - rect.height - offset;
    }

    // Final clamp in case flipping still doesn't fit (e.g. near a corner)
    left = clampToViewport(left, rect.width, window.innerWidth, viewportPadding);
    top = clampToViewport(top, rect.height, window.innerHeight, viewportPadding);

    setPos({ x: left, y: top });
    setIsReady(true);
  }, [x, y]);

  return (
    <div
      ref={ref}
      className="tooltip-overlay"
      style={{
        left: pos?.x ?? x + 10,
        top: pos?.y ?? y + 10,
        opacity: isReady ? 1 : 0,
        transition: 'none'
      }}
    >
      {content}
    </div>
  );
};

// Edge-positioned tooltips for info icons
const EdgeTooltip: React.FC<{
  trigger: HTMLElement;
  content: React.ReactNode;
  position: TooltipPosition;
  offset: number;
  contentClassName: string;
  onMouseEnter: () => void;
  onMouseLeave: () => void;
}> = ({ trigger, content, position, offset, contentClassName, onMouseEnter, onMouseLeave }) => {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null);
  const [isReady, setIsReady] = useState(false);

  // Use useLayoutEffect to calculate position before browser paint
  useLayoutEffect(() => {
    if (!ref.current) return;

    const rect = trigger.getBoundingClientRect();
    const tooltipRect = ref.current.getBoundingClientRect();
    const viewportPadding = 12;
    let x = 0;
    let y = 0;

    // Calculate initial position
    switch (position) {
      case 'top':
        x = rect.left + rect.width / 2 - tooltipRect.width / 2;
        y = rect.top - tooltipRect.height - offset;
        // Flip to bottom if would go off top
        if (y < viewportPadding) {
          y = rect.bottom + offset;
        }
        break;
      case 'bottom':
        x = rect.left + rect.width / 2 - tooltipRect.width / 2;
        y = rect.bottom + offset;
        // Flip to top if would go off bottom
        if (y + tooltipRect.height > window.innerHeight - viewportPadding) {
          y = rect.top - tooltipRect.height - offset;
        }
        break;
      case 'left':
        x = rect.left - tooltipRect.width - offset;
        y = rect.top + rect.height / 2 - tooltipRect.height / 2;
        // Flip to right if would go off left
        if (x < viewportPadding) {
          x = rect.right + offset;
        }
        break;
      case 'right':
        x = rect.right + offset;
        y = rect.top + rect.height / 2 - tooltipRect.height / 2;
        // Flip to left if would go off right
        if (x + tooltipRect.width > window.innerWidth - viewportPadding) {
          x = rect.left - tooltipRect.width - offset;
        }
        break;
    }

    // Clamp to viewport bounds
    x = clampToViewport(x, tooltipRect.width, window.innerWidth, viewportPadding);
    y = clampToViewport(y, tooltipRect.height, window.innerHeight, viewportPadding);

    // Update position and mark as ready - this happens synchronously before paint
    setPos({ x, y });
    setIsReady(true);
  }, [trigger, position, offset]);

  // A long value like a filesystem path is a single unbreakable word, so the max-width caps the
  // box while the text paints straight through it and off the screen. break-words splits a word
  // only when it cannot fit on its own line, so ordinary prose is left exactly as it was.
  //
  // z-300 is the top of the overlay scale, above the drawer (200/201) and the dropdown panels
  // (250/251). Those panels are opaque, so at the old 90 a tooltip on a menu row landed behind
  // the menu. .tooltip-overlay in user.css carries the same value.
  return (
    <div
      ref={ref}
      className={`fixed z-[300] max-w-[min(448px,calc(100vw-24px))] break-words px-2.5 py-1.5 text-xs themed-card text-themed-secondary rounded-md tooltip-edge ${contentClassName}`}
      onMouseEnter={onMouseEnter}
      onMouseLeave={onMouseLeave}
      style={{
        left: pos?.x ?? 0,
        top: pos?.y ?? 0,
        // Use opacity for instant appear/disappear without animation
        opacity: isReady ? 1 : 0,
        // Ensure no transitions that could cause flying effect
        transition: 'none',
        // Prevent interaction during measurement
        pointerEvents: isReady ? 'auto' : 'none'
      }}
    >
      {content}
    </div>
  );
};

export const CacheInfoTooltip: React.FC = () => {
  const isMobile = useMediaQuery('(max-width: 767px)');
  const [globallyDisabled, setGloballyDisabled] = useState(
    document.documentElement.getAttribute('data-disable-tooltips') === 'true'
  );

  // Listen for tooltips setting changes
  useEffect(() => {
    const handleTooltipsChange = () => {
      const disabled = document.documentElement.getAttribute('data-disable-tooltips') === 'true';
      setGloballyDisabled(disabled);
    };
    window.addEventListener(APP_EVENTS.TOOLTIPS_CHANGE, handleTooltipsChange);
    return () => window.removeEventListener(APP_EVENTS.TOOLTIPS_CHANGE, handleTooltipsChange);
  }, []);

  const tooltipsDisabled = globallyDisabled || isMobile;

  return <CacheInfoTooltipInner tooltipsDisabled={tooltipsDisabled} />;
};

const CacheInfoTooltipInner: React.FC<{ tooltipsDisabled: boolean }> = ({ tooltipsDisabled }) => {
  const { t } = useTranslation();

  return (
    <Tooltip
      content={
        <div className="whitespace-normal sm:whitespace-nowrap">
          <span className="cache-hit font-medium">{t('cacheInfo.cacheHits')}</span>
          <span className="text-themed-secondary"> {t('cacheInfo.cacheHitsDesc')}</span>
          <span className="text-themed-muted mx-2">|</span>
          <span className="cache-miss font-medium">{t('cacheInfo.cacheMisses')}</span>
          <span className="text-themed-secondary"> {t('cacheInfo.cacheMissesDesc')}</span>
        </div>
      }
      contentClassName="sm:!max-w-none"
    >
      <Info className={`w-5 h-5 text-themed-muted ${tooltipsDisabled ? '' : 'cursor-help'}`} />
    </Tooltip>
  );
};
