import React, { useState, useRef, useEffect, useLayoutEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { HelpCircle, AlertTriangle, Info, CheckCircle2 } from 'lucide-react';
import { CustomScrollbar } from './CustomScrollbar';
import { useExitPresence, DROPDOWN_EXIT_MS } from '@hooks/useExitPresence';
import { clampToViewport } from '@utils/viewportClamp';

interface HelpPopoverProps {
  /** Rich content as children */
  children?: React.ReactNode;
  /** Popover alignment */
  position?: 'left' | 'right';
  /** Popover width in pixels */
  width?: number;
  /** Max height with scroll */
  maxHeight?: string;
}

/** Gap between the trigger and the popover, whichever side it opens on. */
const TRIGGER_GAP = 8;
/** Smallest gap kept between the popover and the edge of the viewport. */
const VIEWPORT_PADDING = 12;
/**
 * The popover's only chrome outside its scroll viewport is a 1px border, top and
 * bottom. Measuring the content and adding this back is more reliable than
 * measuring the popover itself, whose height is already clipped by whatever
 * limit the previous open applied.
 */
const POPOVER_BORDER = 2;

export const HelpPopover: React.FC<HelpPopoverProps> = ({
  children,
  position = 'left',
  width = 320,
  maxHeight
}) => {
  const { t } = useTranslation();
  const [isOpen, setIsOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const contentRef = useRef<HTMLDivElement>(null);
  const [popoverPos, setPopoverPos] = useState<{ x: number; y: number }>({ x: 0, y: 0 });
  const [availableHeight, setAvailableHeight] = useState<number | null>(null);
  const [isReady, setIsReady] = useState(false);
  const [effectiveWidth, setEffectiveWidth] = useState(width);
  const [viewportSize, setViewportSize] = useState(() => ({
    width: window.innerWidth,
    height: window.innerHeight
  }));
  const { present, closing } = useExitPresence(isOpen, DROPDOWN_EXIT_MS);

  // Calculate effective width based on viewport
  useEffect(() => {
    const calculateWidth = () => {
      const viewportWidth = window.innerWidth;
      setViewportSize({ width: viewportWidth, height: window.innerHeight });
      if (viewportWidth < 640) {
        setEffectiveWidth(Math.min(width, viewportWidth - 32));
      } else {
        setEffectiveWidth(width);
      }
    };

    calculateWidth();
    window.addEventListener('resize', calculateWidth);
    return () => window.removeEventListener('resize', calculateWidth);
  }, [width]);

  const horizontalPosition = useCallback(
    (triggerRect: DOMRect) => {
      const x = position === 'left' ? triggerRect.left : triggerRect.right - effectiveWidth;
      return clampToViewport(x, effectiveWidth, viewportSize.width, VIEWPORT_PADDING);
    },
    [effectiveWidth, position, viewportSize.width]
  );

  // Mount the popover next to its trigger so it measures at its real width. It
  // stays invisible (isReady false) until the layout effect below has decided
  // which side it opens on, so it never paints in one place and jumps.
  const setInitialPopoverPosition = useCallback(() => {
    if (!triggerRef.current) return;

    const triggerRect = triggerRef.current.getBoundingClientRect();
    setPopoverPos({ x: horizontalPosition(triggerRect), y: triggerRect.bottom + TRIGGER_GAP });
  }, [horizontalPosition]);

  // Reset visibility only once the popover has fully unmounted (after the exit
  // animation), so the closing frame keeps its measured position instead of
  // snapping to opacity 0 mid-animation.
  useEffect(() => {
    if (!present) {
      setIsReady(false);
    }
  }, [present]);

  // Close on click outside or Escape
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;
      if (
        popoverRef.current &&
        !popoverRef.current.contains(target) &&
        triggerRef.current &&
        !triggerRef.current.contains(target)
      ) {
        setIsOpen(false);
      }
    };

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      setIsOpen(false);
      // Send focus back to the trigger the popover came from, so keyboard users
      // do not land back at the top of the document.
      triggerRef.current?.focus();
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      document.addEventListener('keydown', handleEscape);
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
        document.removeEventListener('keydown', handleEscape);
      };
    }
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen]);

  // Close on background scroll (but not when scrolling inside the popover)
  useEffect(() => {
    if (!isOpen) return;

    const handleScroll = (e: Event) => {
      if (popoverRef.current?.contains(e.target as Node)) {
        return;
      }
      setIsOpen(false);
    };

    window.addEventListener('scroll', handleScroll, { passive: true, capture: true });
    return () => window.removeEventListener('scroll', handleScroll, { capture: true });
  }, [isOpen]);

  // Place the popover once it is mounted but before it is painted.
  //
  // `present` has to be in the dependency list: the click handler flips `isOpen`
  // while the portal is still unmounted, so this effect's first pass finds
  // `popoverRef` empty and bails. Nothing else in the list changes when the
  // portal then mounts a render later, so without `present` this effect never
  // ran a second time and the popover kept whatever position it was mounted at,
  // hanging off the bottom of short viewports.
  useLayoutEffect(() => {
    if (!isOpen || !present) return;
    if (!triggerRef.current || !popoverRef.current || !contentRef.current) return;

    const triggerRect = triggerRef.current.getBoundingClientRect();
    const naturalHeight = contentRef.current.getBoundingClientRect().height + POPOVER_BORDER;

    // Space to the viewport edge decides whether a side fits. The viewport
    // padding is only taken off once the popover has to be clamped, so help
    // text that already fitted, even by a couple of pixels, stays where it was
    // instead of flipping or growing a scrollbar it never needed.
    // Shrinking the window while the popover is open can leave the trigger below
    // the new bottom edge. Capping the space above at the viewport keeps those
    // already-hidden rows out of the measurement.
    const spaceBelow = viewportSize.height - triggerRect.bottom - TRIGGER_GAP;
    const spaceAbove = Math.min(triggerRect.top, viewportSize.height) - TRIGGER_GAP;

    let opensBelow = true;
    let room = spaceBelow;
    if (naturalHeight > spaceBelow) {
      if (naturalHeight <= spaceAbove) {
        opensBelow = false;
        room = spaceAbove;
      } else {
        // Taller than either side, which is the normal case for long help text
        // on a phone: take the roomier side and scroll the remainder, because
        // flipping alone only buys the trigger's distance from the top.
        opensBelow = spaceBelow >= spaceAbove;
        room = (opensBelow ? spaceBelow : spaceAbove) - VIEWPORT_PADDING;
      }
    }

    const renderedHeight = Math.min(naturalHeight, room);
    // Opening upwards normally ends at the trigger, which is on screen. When the
    // trigger itself has dropped past the bottom edge, sitting against it would
    // hang the last lines of help text off the screen with nothing to scroll.
    // The bottom edge has to clear the viewport padding, but the top edge is only
    // floored at 0 below, so a popover that just fits above its trigger keeps the
    // position it had. Clamping inside the padded region expresses both ends.
    const y = opensBelow
      ? triggerRect.bottom + TRIGGER_GAP
      : clampToViewport(
          triggerRect.top - renderedHeight - TRIGGER_GAP,
          renderedHeight,
          viewportSize.height - VIEWPORT_PADDING,
          0
        );

    setPopoverPos({ x: horizontalPosition(triggerRect), y: Math.max(0, y) });
    setAvailableHeight(Math.max(0, Math.floor(room) - POPOVER_BORDER));
    setIsReady(true);
  }, [isOpen, present, horizontalPosition, viewportSize.height]);

  // The measured room on the open side, narrowed further by a caller's own limit.
  const heightLimit =
    availableHeight === null
      ? maxHeight
      : maxHeight
        ? `min(${maxHeight}, ${availableHeight}px)`
        : `${availableHeight}px`;

  return (
    <>
      <button
        ref={triggerRef}
        aria-label={t('common.help')}
        onClick={() => {
          if (!isOpen) {
            setInitialPopoverPosition();
            setIsOpen(true);
          } else {
            setIsOpen(false);
          }
        }}
        className={`p-1 rounded-md transition-colors ${
          isOpen
            ? 'text-[var(--theme-primary)] bg-[var(--theme-primary-subtle)]'
            : 'text-themed-secondary bg-transparent hover:bg-themed-hover'
        }`}
      >
        <HelpCircle className="w-4 h-4" />
      </button>

      {present &&
        createPortal(
          <div
            ref={popoverRef}
            className={`fixed themed-border-radius-sm border help-popover themed-card max-w-[calc(100vw-24px)] z-[90] ${
              closing
                ? 'animate-[dropdownSlideOutDown_0.14s_ease-in_forwards]'
                : isReady
                  ? 'animate-[dropdownSlideDown_0.15s_cubic-bezier(0.16,1,0.3,1)]'
                  : ''
            }`}
            style={{
              left: popoverPos.x,
              top: popoverPos.y,
              width: effectiveWidth,
              // Before the first measurement this keeps a tall popover roughly
              // on screen; afterwards the scroll viewport owns the limit, and a
              // second cap out here would clip the border box two pixels short.
              maxHeight: availableHeight === null ? maxHeight || `calc(100vh - 100px)` : undefined,
              opacity: isReady ? 1 : 0,
              transition: 'none',
              pointerEvents: isReady && !closing ? 'auto' : 'none'
            }}
          >
            <CustomScrollbar maxHeight={heightLimit}>
              <div ref={contentRef} className="p-4 sm:p-5">
                <div className="help-popover-sections text-xs leading-relaxed text-themed-secondary">
                  {children}
                </div>
              </div>
            </CustomScrollbar>
          </div>,
          document.body
        )}
    </>
  );
};

/** Section with title in HelpPopover */
export const HelpSection: React.FC<{
  title: string;
  children: React.ReactNode;
  variant?: 'default' | 'subtle';
}> = ({ title, children, variant = 'default' }) => (
  <div className={variant === 'subtle' ? 'help-section-subtle' : ''}>
    <div className="help-section-title">{title}</div>
    <div className="text-xs leading-relaxed text-themed-secondary">{children}</div>
  </div>
);

type HelpNoteType = 'info' | 'warning' | 'success' | 'tip';

// Full literal class names so Tailwind's content scanner keeps these @layer components
// rules in the production build. Building them dynamically (e.g. `help-note-${type}`)
// hides the class strings from the scanner, which then purges the per-type background
// and border rules and every note renders as a plain untinted box.
const HELP_NOTE_CLASS: Record<HelpNoteType, string> = {
  info: 'help-note help-note-info',
  warning: 'help-note help-note-warning',
  success: 'help-note help-note-success',
  tip: 'help-note help-note-tip'
};

/** Note/callout box in HelpPopover */
export const HelpNote: React.FC<{
  children: React.ReactNode;
  type?: HelpNoteType;
}> = ({ children, type = 'info' }) => {
  const iconMap = {
    info: Info,
    warning: AlertTriangle,
    success: CheckCircle2,
    tip: Info
  };

  const iconColorMap = {
    info: 'text-themed-info',
    warning: 'text-themed-warning',
    success: 'text-themed-success',
    tip: 'icon-purple'
  };

  const Icon = iconMap[type];

  return (
    <div className={HELP_NOTE_CLASS[type]}>
      <Icon className={`w-3.5 h-3.5 flex-shrink-0 mt-0.5 ${iconColorMap[type]}`} />
      <div className="text-themed-primary">{children}</div>
    </div>
  );
};

interface HelpDefinitionItem {
  term: string;
  description: string;
}

/** Definition list replacing the divide-y pattern */
export const HelpDefinition: React.FC<{
  items: HelpDefinitionItem[];
}> = ({ items }) => (
  <div className="help-definition-list">
    {items.map((item: HelpDefinitionItem) => (
      <div key={item.term}>
        <div className="help-definition-term">{item.term}</div>
        <div className="help-definition-desc">{item.description}</div>
      </div>
    ))}
  </div>
);
