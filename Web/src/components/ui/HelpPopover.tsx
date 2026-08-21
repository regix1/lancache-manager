import React, { useState, useRef, useEffect, useLayoutEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { HelpCircle, AlertTriangle, Info, CheckCircle2 } from 'lucide-react';
import { CustomScrollbar } from './CustomScrollbar';
import { useAnchoredPanel, type PanelPlacement, type PanelSpace } from '@hooks/useAnchoredPanel';
import { clampToViewport, POPOVER_GUTTER_PX } from '@utils/viewportClamp';

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
  const [isReady, setIsReady] = useState(false);
  const [effectiveWidth, setEffectiveWidth] = useState(width);

  // Calculate effective width based on viewport
  useEffect(() => {
    const calculateWidth = () => {
      const viewportWidth = window.innerWidth;
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

  /**
   * The popover sizes itself into the room on the side it opens on, so it decides
   * both at once and the shared below/above placement cannot be used. The side is
   * chosen from the CONTENT's natural height rather than the popover's own, because
   * the popover has already been clipped to whatever limit the previous open applied.
   */
  const place = useCallback(
    (space: PanelSpace): PanelPlacement => {
      const { anchor, viewportWidth, viewportHeight } = space;
      const naturalHeight =
        (contentRef.current?.getBoundingClientRect().height ?? 0) + POPOVER_BORDER;

      // Space to the viewport edge decides whether a side fits. The viewport
      // padding is only taken off once the popover has to be clamped, so help
      // text that already fitted, even by a couple of pixels, stays where it was
      // instead of flipping or growing a scrollbar it never needed.
      // Shrinking the window while the popover is open can leave the trigger below
      // the new bottom edge. Capping the space above at the viewport keeps those
      // already-hidden rows out of the measurement.
      const spaceBelow = viewportHeight - anchor.bottom - TRIGGER_GAP;
      const spaceAbove = Math.min(anchor.top, viewportHeight) - TRIGGER_GAP;

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
          room = (opensBelow ? spaceBelow : spaceAbove) - POPOVER_GUTTER_PX;
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
        ? anchor.bottom + TRIGGER_GAP
        : clampToViewport(
            anchor.top - renderedHeight - TRIGGER_GAP,
            renderedHeight,
            viewportHeight - POPOVER_GUTTER_PX,
            0
          );

      const x = position === 'left' ? anchor.left : anchor.right - effectiveWidth;

      return {
        top: Math.max(0, y),
        left: clampToViewport(x, effectiveWidth, viewportWidth, POPOVER_GUTTER_PX),
        openUpward: !opensBelow,
        // Settles after one pass: the content's natural height does not change when the
        // scroll viewport around it shrinks, so the same room is measured again.
        availableHeight: Math.max(0, Math.floor(room) - POPOVER_BORDER)
      };
    },
    [effectiveWidth, position]
  );

  const closePopover = useCallback((): void => setIsOpen(false), []);

  // Escape sends focus back to the trigger it came from, so a keyboard reader does not
  // land at the top of the document. This cannot be `onClose`'s job: the hook also closes
  // the popover once the trigger has scrolled out of view, and focusing a trigger there
  // would drag the page back to it.
  const returnFocusToTrigger = useCallback((): void => {
    triggerRef.current?.focus();
  }, []);

  const {
    present,
    closing,
    position: popoverPos
  } = useAnchoredPanel({
    open: isOpen,
    anchorRef: triggerRef,
    panelRef: popoverRef,
    onClose: closePopover,
    onEscape: returnFocusToTrigger,
    gutter: POPOVER_GUTTER_PX,
    place
  });

  const { availableHeight } = popoverPos;

  // The hook places the popover in a layout effect, so the commit that mounts it already
  // carries a measured position and it can be shown before the browser paints. Hiding it
  // again waits for the unmount, so the closing frame keeps that position instead of
  // snapping to opacity 0 mid-animation.
  useLayoutEffect(() => {
    setIsReady(present);
  }, [present]);

  // Click-outside only. Escape belongs to the shared hook, and there is no scroll
  // handler: the popover is positioned on the page and carried by its trigger, so a
  // scroll can no longer misposition it, and it dismisses once the trigger is gone.
  useEffect(() => {
    if (!isOpen) return;

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

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen]);

  // The measured room on the open side, narrowed further by a caller's own limit.
  const heightLimit =
    availableHeight === undefined
      ? maxHeight
      : maxHeight
        ? `min(${maxHeight}, ${availableHeight}px)`
        : `${availableHeight}px`;

  return (
    <>
      <button
        ref={triggerRef}
        aria-label={t('common.help')}
        onClick={() => setIsOpen((open) => !open)}
        className={`help-popover-trigger p-1 rounded-md transition-colors ${
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
            className={`absolute themed-border-radius-sm border help-popover themed-card max-w-[calc(100vw-24px)] z-[90] motion-reduce:animate-none ${
              closing
                ? 'animate-[dropdownSlideOutDown_0.14s_ease-in_forwards]'
                : isReady
                  ? 'animate-[dropdownSlideDown_0.15s_cubic-bezier(0.16,1,0.3,1)]'
                  : ''
            }`}
            style={{
              left: popoverPos.left,
              top: popoverPos.top,
              width: effectiveWidth,
              // Before the first measurement this keeps a tall popover roughly
              // on screen; afterwards the scroll viewport owns the limit, and a
              // second cap out here would clip the border box two pixels short.
              maxHeight:
                availableHeight === undefined ? maxHeight || `calc(100vh - 100px)` : undefined,
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
