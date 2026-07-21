import React, { useState, useEffect, useRef, useLayoutEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, ChevronRight, Check } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { CustomScrollbar } from './CustomScrollbar';
import { Tooltip } from './Tooltip';
import { getEventColorVar } from '@utils/eventColors';
import { useExitPresence, DROPDOWN_EXIT_MS } from '@hooks/useExitPresence';
import { useAnchorFollow, readAnchorRect, type AnchorRect } from '@hooks/useAnchorFollow';

/**
 * Menu placement in DOCUMENT coordinates (the menu is `position: absolute` in a
 * body portal, so `top`/`left` are page offsets, not viewport offsets). Positioning
 * the menu on the page rather than in the viewport is what lets an ordinary scroll
 * carry the menu and its trigger together natively - see useAnchorFollow. `width` is
 * the trigger's width, used as the menu's minimum width.
 */
interface DropdownPosition {
  top: number;
  left: number;
  width: number;
}

/** Menu placement resolved from a trigger rect: `upward` records which side it opened on. */
interface MenuGeometry extends DropdownPosition {
  upward: boolean;
}

interface SubmenuGeometry {
  top: number;
  left: number;
  openLeft: boolean;
}

const VIEWPORT_PADDING_PX = 8;
const DROPDOWN_MAX_WIDTH_MARGIN_PX = 32; // Matches `max-w-[calc(100vw-32px)]`
const MENU_GAP_PX = 4;
const SUBMENU_WIDTH_PX = 256;
const SUBMENU_GAP_PX = 4;
/** Position deltas at or below this are rounding noise, not movement. */
const POSITION_EPSILON_PX = 0.5;
/**
 * Extra room the losing side must offer before the menu flips direction. Without
 * it, a trigger sitting exactly at the fits/doesn't-fit boundary would flip
 * up/down every frame the page reflows by a pixel.
 */
const DIRECTION_FLIP_HYSTERESIS_PX = 8;

/**
 * Picks the side to open on, biased toward staying put.
 *
 * Downward is the preferred side. On first open the menu goes down unless it
 * would overflow and up is less cramped. Once it is open the side is sticky: it
 * only changes when the other side is materially better (see
 * DIRECTION_FLIP_HYSTERESIS_PX), so a menu being carried around by a reflow
 * never oscillates. Sides are ranked by how far the menu overflows each one,
 * rather than a bare fits/doesn't-fit test, so a menu too tall for either side
 * still lands on the roomier one.
 */
function chooseUpward(
  current: boolean | null,
  spaceAbove: number,
  spaceBelow: number,
  menuHeight: number
): boolean {
  const overflowBelow = Math.max(0, menuHeight - spaceBelow);
  const overflowAbove = Math.max(0, menuHeight - spaceAbove);

  if (current === null) {
    return overflowBelow > 0 && overflowAbove < overflowBelow;
  }

  if (current) {
    // Upward: fall back to the preferred side as soon as it has clear room. The
    // margin is what stops a menu resting on the boundary from flipping back and
    // forth as the page reflows by a pixel.
    return !(spaceBelow >= menuHeight + DIRECTION_FLIP_HYSTERESIS_PX);
  }

  // Downward: give up the preferred side only when upward is materially roomier.
  return overflowAbove + DIRECTION_FLIP_HYSTERESIS_PX < overflowBelow;
}

function isSameDropdownPosition(a: DropdownPosition, b: DropdownPosition): boolean {
  return (
    Math.abs(a.top - b.top) <= POSITION_EPSILON_PX &&
    Math.abs(a.left - b.left) <= POSITION_EPSILON_PX &&
    Math.abs(a.width - b.width) <= POSITION_EPSILON_PX
  );
}

function isSameSubmenuPosition(a: SubmenuGeometry, b: SubmenuGeometry): boolean {
  return (
    Math.abs(a.top - b.top) <= POSITION_EPSILON_PX &&
    Math.abs(a.left - b.left) <= POSITION_EPSILON_PX &&
    a.openLeft === b.openLeft
  );
}

/**
 * Places the submenu beside its option row, flipping left when the right edge is
 * tight. Returned in document coordinates, like the menu itself.
 */
function computeSubmenuGeometry(triggerRect: DOMRect): SubmenuGeometry {
  const spaceOnRight = window.innerWidth - triggerRect.right;
  const openLeft = spaceOnRight < SUBMENU_WIDTH_PX && triggerRect.left > SUBMENU_WIDTH_PX;
  const left = openLeft
    ? triggerRect.left - SUBMENU_WIDTH_PX - SUBMENU_GAP_PX
    : triggerRect.right + SUBMENU_GAP_PX;

  return {
    top: triggerRect.top + window.scrollY,
    left: left + window.scrollX,
    openLeft
  };
}

function getRootFontSizePx(): number {
  const fontSize = window.getComputedStyle(document.documentElement).fontSize;
  const parsed = Number.parseFloat(fontSize);
  return Number.isFinite(parsed) ? parsed : 16;
}

function resolveCssWidthToPx(value: string, fallbackPx: number, rootFontSizePx: number): number {
  const trimmed = value.trim();

  const pxMatch = trimmed.match(/^(\d+(?:\.\d+)?)px$/);
  if (pxMatch) return Number.parseFloat(pxMatch[1]);

  const remMatch = trimmed.match(/^(\d+(?:\.\d+)?)rem$/);
  if (remMatch) return Number.parseFloat(remMatch[1]) * rootFontSizePx;

  const percentMatch = trimmed.match(/^(\d+(?:\.\d+)?)%$/);
  if (percentMatch) return (Number.parseFloat(percentMatch[1]) / 100) * window.innerWidth;

  const vwMatch = trimmed.match(/^(\d+(?:\.\d+)?)vw$/);
  if (vwMatch) return (Number.parseFloat(vwMatch[1]) / 100) * window.innerWidth;

  const numeric = Number.parseFloat(trimmed);
  if (Number.isFinite(numeric)) return numeric;

  return fallbackPx;
}

function resolveDropdownWidthToPx(dropdownWidth: string | undefined, fallbackPx: number): number {
  if (!dropdownWidth) return fallbackPx;

  const widthToken = dropdownWidth
    .trim()
    .split(/\s+/)
    .find((token) => token.startsWith('w-'));

  if (!widthToken) {
    // Treat as CSS width value (e.g. "280px", "18rem")
    return resolveCssWidthToPx(dropdownWidth, fallbackPx, getRootFontSizePx());
  }

  // Tailwind width classes (common cases used in this app)
  if (widthToken === 'w-full' || widthToken === 'w-screen') {
    return Math.max(fallbackPx, window.innerWidth - DROPDOWN_MAX_WIDTH_MARGIN_PX);
  }

  const bracketMatch = widthToken.match(/^w-\[(.+)\]$/);
  if (bracketMatch) {
    return resolveCssWidthToPx(bracketMatch[1], fallbackPx, getRootFontSizePx());
  }

  const numericMatch = widthToken.match(/^w-(\d+)$/);
  if (numericMatch) {
    const scale = Number.parseInt(numericMatch[1], 10);
    if (Number.isFinite(scale)) {
      // Tailwind spacing scale: 1 = 0.25rem
      return scale * (getRootFontSizePx() / 4);
    }
  }

  return fallbackPx;
}

interface IconComponentProps {
  size?: number;
  className?: string;
  style?: React.CSSProperties;
}

export interface SubmenuOption {
  value: string;
  label: string;
  description?: string;
  color?: string;
  colorIndex?: number;
  badge?: string;
  badgeColor?: string;
}

export interface DropdownOption {
  value: string;
  label: string;
  shortLabel?: string;
  description?: string;
  tooltip?: string;
  icon?: React.ComponentType<IconComponentProps>;
  disabled?: boolean;
  rightLabel?: string;
  submenu?: SubmenuOption[];
  submenuTitle?: string;
}

interface EnhancedDropdownProps {
  options: DropdownOption[];
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  className?: string;
  disabled?: boolean;
  compactMode?: boolean;
  customTriggerLabel?: string;
  triggerIcon?: React.ComponentType<IconComponentProps>;
  triggerAriaLabel?: string;
  iconOnly?: boolean;
  prefix?: string;
  dropdownWidth?: string;
  alignRight?: boolean;
  dropdownTitle?: string;
  footerNote?: string;
  footerIcon?: React.ComponentType<IconComponentProps>;
  cleanStyle?: boolean;
  maxHeight?: string;
  /** Trigger button style variant. 'card' = dark card bg (default, for headers/nav). 'button' = matches Button component (lighter, for toolbars). */
  variant?: 'card' | 'button';
  /**
   * Trigger height (desktop). Explicit per size - these used to be padding-only classes
   * (`py-1.5`/`py-[9px]`/`py-2.5`) whose actual height was an emergent sum of padding +
   * border + line-height, verified to equal the values below via getBoundingClientRect but
   * with nothing stopping it drifting a device-pixel from an adjacent explicit-height
   * control (the same flaw fixed in SegmentedControl/ToggleSwitch). Now a fixed `h-*`:
   *   sm = 34px · md (default) = 40px, height-matched to Button md/SegmentedControl md ·
   *   lg = 42px (kept for any call site relying on the taller trigger; not matched to
   *   anything else in the app's size scale).
   * Below the 640px/400px breakpoints, dropdowns.css overrides `.ed-trigger` back to
   * `height: auto` and shrinks the padding instead - mobile intentionally renders shorter
   * than any of these three values, so it isn't pinned to them.
   */
  size?: 'sm' | 'md' | 'lg';
}

export const EnhancedDropdown: React.FC<EnhancedDropdownProps> = ({
  options,
  value,
  onChange,
  placeholder,
  className = '',
  disabled = false,
  compactMode = false,
  customTriggerLabel,
  triggerIcon: TriggerIconOverride,
  triggerAriaLabel,
  iconOnly = false,
  prefix,
  dropdownWidth,
  alignRight = false,
  dropdownTitle,
  footerNote,
  footerIcon: FooterIcon,
  cleanStyle = false,
  maxHeight,
  variant = 'card',
  size = 'md'
}) => {
  const { t } = useTranslation();
  const [isOpen, setIsOpen] = useState(false);
  const { present, closing } = useExitPresence(isOpen, DROPDOWN_EXIT_MS);
  const [dropdownStyle, setDropdownStyle] = useState<{ animation: string }>({ animation: '' });
  const [dropdownPosition, setDropdownPosition] = useState<DropdownPosition | null>(null);
  const [expandedSubmenu, setExpandedSubmenu] = useState<string | null>(null);
  const [submenuPosition, setSubmenuPosition] = useState<SubmenuGeometry | null>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const submenuRef = useRef<HTMLDivElement>(null);
  /** The option row the open submenu hangs off, so it can be re-measured as the menu moves. */
  const submenuTriggerRef = useRef<HTMLButtonElement | null>(null);
  /** Side the menu is currently opened on (null while closed). Feeds the flip hysteresis. */
  const upwardRef = useRef<boolean | null>(null);

  const selectedOption =
    options.find((opt) => opt.value === value) ||
    (value.includes(':')
      ? options.find((opt) => opt.submenu && value.startsWith(opt.value + ':'))
      : undefined);

  useEffect(() => {
    if (!isOpen) {
      // Submenu closes immediately; the main menu keeps its position through the
      // exit animation and is cleared once fully unmounted (see the effect below).
      setExpandedSubmenu(null);
      setSubmenuPosition(null);
      submenuTriggerRef.current = null;
    }
  }, [isOpen]);

  // Clear the cached position only after the exit animation has unmounted the
  // menu, so `closing` renders keep their coordinates and the exit is stable.
  useEffect(() => {
    if (!present) {
      setDropdownPosition(null);
      upwardRef.current = null;
    }
  }, [present]);

  /**
   * Resolves where the menu sits for a given trigger rect. Before the menu is in
   * the DOM this runs off an estimated height/width; once it is mounted the
   * measured box is used, which is what makes the direction and the horizontal
   * clamp exact.
   */
  const computeMenuGeometry = useCallback(
    (anchor: AnchorRect): MenuGeometry => {
      // offsetWidth/offsetHeight, NOT getBoundingClientRect: the entrance keyframes
      // scale the menu (`scale(0.98)`), and a bounding rect measured mid-animation
      // reports that scaled size. An upward menu is placed by subtracting its height
      // from the trigger's top, so a height ~2% short parks it right on top of the
      // button. The offset box is the layout size and ignores transforms.
      const menuEl = dropdownRef.current;
      const measuredHeight = menuEl?.offsetHeight ?? 0;
      const measuredWidth = menuEl?.offsetWidth ?? 0;

      const parsedMaxHeight = maxHeight && maxHeight.endsWith('px') ? parseInt(maxHeight, 10) : 300;
      const estimatedContentHeight = compactMode
        ? Math.min(parsedMaxHeight, options.length * 24 + 8)
        : parsedMaxHeight;
      const menuHeight = measuredHeight > 0 ? measuredHeight : estimatedContentHeight + 50;
      const menuWidth =
        measuredWidth > 0 ? measuredWidth : resolveDropdownWidthToPx(dropdownWidth, anchor.width);

      // Collisions are decided in viewport space (that is what the menu has to fit
      // inside), then translated to the page so the result scrolls with the trigger.
      const viewportWidth = document.documentElement.clientWidth || window.innerWidth;
      const viewportHeight = window.innerHeight;
      const spaceBelow = viewportHeight - anchor.bottom - MENU_GAP_PX;
      const spaceAbove = anchor.top - MENU_GAP_PX;
      const upward = chooseUpward(upwardRef.current, spaceAbove, spaceBelow, menuHeight);

      const desiredLeft = alignRight ? anchor.right - menuWidth : anchor.left;
      const maxLeft = Math.max(
        VIEWPORT_PADDING_PX,
        viewportWidth - menuWidth - VIEWPORT_PADDING_PX
      );
      const left = Math.min(Math.max(desiredLeft, VIEWPORT_PADDING_PX), maxLeft);

      // A single top edge for both directions: an upward menu hangs its bottom edge
      // off the trigger's top. Anchoring by `bottom` instead would be measured from
      // the bottom of the *document* once the menu is absolutely positioned.
      const top = upward ? anchor.top - MENU_GAP_PX - menuHeight : anchor.bottom + MENU_GAP_PX;

      return {
        top: top + window.scrollY,
        left: left + window.scrollX,
        width: anchor.width,
        upward
      };
    },
    [alignRight, dropdownWidth, maxHeight, compactMode, options.length]
  );

  /** Re-measures the open submenu against its (possibly moved) option row. */
  const syncSubmenuPosition = useCallback((): void => {
    const trigger = submenuTriggerRef.current;
    if (!trigger) return;

    const next = computeSubmenuGeometry(trigger.getBoundingClientRect());
    setSubmenuPosition((prev) => (prev && isSameSubmenuPosition(prev, next) ? prev : next));
  }, []);

  /**
   * Writes a freshly computed placement to state. `allowAnimationUpdate` is only
   * set while the menu is entering - a direction flip mid-follow must not replay
   * the entrance keyframe under the user's cursor.
   */
  const applyMenuGeometry = useCallback(
    (anchor: AnchorRect, allowAnimationUpdate: boolean): void => {
      const geometry = computeMenuGeometry(anchor);
      const directionChanged = upwardRef.current !== null && upwardRef.current !== geometry.upward;
      upwardRef.current = geometry.upward;

      const next: DropdownPosition = {
        top: geometry.top,
        left: geometry.left,
        width: geometry.width
      };
      setDropdownPosition((prev) => (prev && isSameDropdownPosition(prev, next) ? prev : next));

      if (allowAnimationUpdate && directionChanged) {
        setDropdownStyle({
          animation: `${geometry.upward ? 'dropdownSlideUp' : 'dropdownSlideDown'} 0.15s cubic-bezier(0.16, 1, 0.3, 1)`
        });
      }
    },
    [computeMenuGeometry]
  );

  // Correct the open-time estimate with the menu's real measured box before the
  // browser paints it, so the menu never visibly jumps into place.
  useLayoutEffect(() => {
    if (!isOpen || !buttonRef.current) return;
    applyMenuGeometry(readAnchorRect(buttonRef.current), true);
  }, [isOpen, applyMenuGeometry]);

  // The parent menu's coordinates are state-driven. Measure the submenu row only
  // after React has committed those coordinates, while still correcting the
  // separately portalled submenu before the browser paints.
  useLayoutEffect(() => {
    if (!expandedSubmenu || !dropdownPosition) return;
    syncSubmenuPosition();
  }, [expandedSubmenu, dropdownPosition, syncSubmenuPosition]);

  /**
   * Keeps the portalled menu glued to its trigger while it is on screen. The page
   * reflows underneath it whenever UniversalNotificationBar (an in-flow sticky
   * bar) shows or finishes an operation, which moves the trigger without firing
   * a scroll or resize event - so the menu follows the trigger's rect instead of
   * listening for events. The submenu hangs off a row inside the menu and is
   * re-measured by the layout effect after the parent's new position is committed.
   */
  const handleAnchorMove = useCallback(
    (anchor: AnchorRect): void => {
      applyMenuGeometry(anchor, false);
    },
    [applyMenuGeometry]
  );

  /** Nothing left to anchor to once the trigger is scrolled off screen. */
  const handleAnchorLost = useCallback((): void => {
    setIsOpen(false);
  }, []);

  useAnchorFollow({
    enabled: present,
    anchorRef: buttonRef,
    onAnchorMove: handleAnchorMove,
    onAnchorLost: handleAnchorLost
  });

  // Event listeners
  useEffect(() => {
    if (!isOpen) return;

    const handleClickOutside = (e: MouseEvent) => {
      const target = e.target as Node;
      if (
        !dropdownRef.current?.contains(target) &&
        !buttonRef.current?.contains(target) &&
        !submenuRef.current?.contains(target)
      ) {
        setIsOpen(false);
      }
    };

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };

    // No scroll handler: the menu follows its trigger (see useAnchorFollow), so
    // scrolling no longer mispositions it, and it dismisses itself if the trigger
    // leaves the viewport. Closing on `scroll` was also actively harmful here -
    // inserting the notification bar above the viewport makes the browser's scroll
    // anchoring adjust scrollTop, which fires a scroll event and used to close an
    // open menu even though nothing had visibly moved.
    document.addEventListener('mousedown', handleClickOutside);
    document.addEventListener('keydown', handleEscape);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
      document.removeEventListener('keydown', handleEscape);
    };
  }, [isOpen]);

  const handleSelect = useCallback(
    (optionValue: string) => {
      onChange(optionValue);
      setIsOpen(false);
    },
    [onChange]
  );

  const handleSubmenuToggle = useCallback(
    (optionValue: string, triggerElement: HTMLButtonElement) => {
      if (expandedSubmenu === optionValue) {
        setExpandedSubmenu(null);
        setSubmenuPosition(null);
        submenuTriggerRef.current = null;
      } else {
        // Remember the row itself, not just its rect: the menu moves whenever the
        // page reflows, and the submenu is re-measured off this element.
        submenuTriggerRef.current = triggerElement;
        setSubmenuPosition(computeSubmenuGeometry(triggerElement.getBoundingClientRect()));
        setExpandedSubmenu(optionValue);
      }
    },
    [expandedSubmenu]
  );

  const displayLabel = customTriggerLabel
    ? customTriggerLabel
    : selectedOption
      ? (prefix ? `${prefix} ` : '') +
        (compactMode && selectedOption.shortLabel
          ? selectedOption.shortLabel
          : selectedOption.label)
      : placeholder || t('ui.dropdown.selectOption');
  const TriggerIcon = TriggerIconOverride ?? selectedOption?.icon;
  const resolvedAriaLabel = triggerAriaLabel || displayLabel;
  // Size → explicit trigger height (height matrix in the `size` prop doc above). `items-center`
  // on the button centers the icon/label/chevron within it, same as Button/SegmentedControl.
  const triggerSizeClass = size === 'sm' ? 'h-[34px]' : size === 'lg' ? 'h-[42px]' : 'h-10';

  // While closing, swap the entrance keyframe for its exit mirror in the same
  // direction. Both directions are anchored by `top`, so the side the menu opened
  // on is carried by upwardRef rather than inferred from the coordinates.
  const isUpwardMenu = upwardRef.current === true;
  const menuAnimation = closing
    ? `${isUpwardMenu ? 'dropdownSlideOutUp' : 'dropdownSlideOutDown'} 0.14s ease-in forwards`
    : dropdownStyle.animation;

  return (
    <div className={`relative ${className}`}>
      <button
        ref={buttonRef}
        type="button"
        onClick={(e) => {
          e.stopPropagation();
          if (disabled) return;
          if (!isOpen) {
            if (buttonRef.current) {
              // Seed the menu from an estimated size so it has coordinates to
              // render at; the layout effect corrects it from the measured box
              // before paint.
              const geometry = computeMenuGeometry(readAnchorRect(buttonRef.current));
              upwardRef.current = geometry.upward;
              setDropdownPosition({
                top: geometry.top,
                left: geometry.left,
                width: geometry.width
              });
              setDropdownStyle({
                animation: `${geometry.upward ? 'dropdownSlideUp' : 'dropdownSlideDown'} 0.15s cubic-bezier(0.16, 1, 0.3, 1)`
              });
            }
            setIsOpen(true);
          } else {
            setIsOpen(false);
          }
        }}
        onTouchEnd={(e) => {
          e.stopPropagation();
        }}
        disabled={disabled}
        aria-label={resolvedAriaLabel}
        className={`ed-trigger w-full px-3 ${triggerSizeClass} themed-border-radius-sm border text-left flex items-center justify-between text-sm text-themed-primary ${
          variant === 'button' ? 'bg-themed-surface hover:bg-themed-surface-hover' : 'themed-card'
        } ${
          isOpen
            ? 'ed-trigger--open border-themed-focus'
            : variant === 'button'
              ? 'border-themed-secondary'
              : 'border-themed-primary'
        } ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}`}
      >
        <div
          className={`flex items-center flex-1 truncate ${iconOnly ? 'justify-center' : 'gap-1.5'}`}
        >
          {TriggerIcon && (
            <TriggerIcon className="flex-shrink-0 text-[var(--theme-primary)]" size={16} />
          )}
          {!iconOnly && (
            <span className={compactMode ? 'font-medium' : 'truncate'}>{displayLabel}</span>
          )}
        </div>
        {!iconOnly && (
          <ChevronDown
            size={16}
            className={`flex-shrink-0 transition-transform duration-200 text-themed-primary ${isOpen ? 'rotate-180' : ''}`}
          />
        )}
      </button>

      {/* Dropdown - rendered via portal to escape stacking context.
          Rendered while `present` (not just `isOpen`) so the exit animation plays. */}
      {present &&
        dropdownPosition &&
        createPortal(
          <div
            ref={dropdownRef}
            className={`ed-dropdown ed-dropdown--menu absolute themed-border-radius-sm border border-themed-primary overflow-hidden bg-themed-secondary max-w-[calc(100vw-32px)] z-[250] ${dropdownWidth?.trim().startsWith('w-') ? dropdownWidth : ''}`}
            style={{
              top: dropdownPosition.top,
              left: dropdownPosition.left,
              ...(dropdownWidth && !dropdownWidth.trim().startsWith('w-')
                ? { width: dropdownWidth }
                : !dropdownWidth
                  ? { width: dropdownPosition.width }
                  : {}),
              ...(!dropdownWidth ? { minWidth: dropdownPosition.width } : {}),
              animation: menuAnimation,
              pointerEvents: closing ? 'none' : undefined
            }}
          >
            {dropdownTitle && (
              <div className="px-3 py-2 text-sm font-medium border-b border-themed-primary bg-themed-secondary text-themed-secondary">
                {dropdownTitle}
              </div>
            )}

            <CustomScrollbar
              maxHeight={cleanStyle ? 'none' : maxHeight || '280px'}
              variant="float"
              className="!rounded-none"
            >
              {/* No vertical padding: the first/last option's highlight must reach the
                  panel edge, where the rounded overflow clip finishes the corners. */}
              <div>
                {options.map((option) =>
                  option.value === 'divider' ? (
                    <div
                      key={option.value}
                      className="px-3 py-2 text-xs font-medium border-t border-themed-primary mt-1 mb-1 truncate text-themed-muted bg-themed-tertiary"
                    >
                      {option.label}
                    </div>
                  ) : option.submenu && option.submenu.length > 0 ? (
                    <React.Fragment key={option.value}>
                      <Tooltip
                        content={option.description || option.label}
                        position="top"
                        className="w-full"
                      >
                        <button
                          type="button"
                          onClick={(e) => handleSubmenuToggle(option.value, e.currentTarget)}
                          className={`ed-option w-full ${compactMode ? 'px-2 py-1 text-xs' : 'px-3 py-2.5 text-sm'} text-left cursor-pointer ${value.startsWith(option.value + ':') || expandedSubmenu === option.value ? 'ed-option-selected' : ''}`}
                        >
                          <div className="flex items-start gap-3">
                            {!cleanStyle && option.icon && (
                              <option.icon
                                className={`flex-shrink-0 mt-0.5 ${
                                  value.startsWith(option.value + ':')
                                    ? 'text-[var(--theme-selected-text)]'
                                    : 'text-themed-secondary'
                                }`}
                                size={16}
                              />
                            )}
                            <div className="flex flex-col flex-1 min-w-0">
                              <span
                                className={`font-medium truncate ${value.startsWith(option.value + ':') ? 'text-[var(--theme-selected-text)]' : 'text-themed-primary'}`}
                              >
                                {option.label}
                              </span>
                              {option.description && (
                                <span className="text-xs mt-0.5 leading-relaxed text-themed-secondary">
                                  {option.description}
                                </span>
                              )}
                            </div>
                            {option.rightLabel && (
                              <span
                                className={`flex-shrink-0 text-xs font-medium ${
                                  value.startsWith(option.value + ':')
                                    ? 'text-[var(--theme-selected-text)]'
                                    : 'text-themed-secondary'
                                }`}
                              >
                                {option.rightLabel}
                              </span>
                            )}
                            <ChevronRight
                              size={16}
                              className={`flex-shrink-0 mt-0.5 transition-transform duration-200 text-themed-muted ${expandedSubmenu === option.value ? (submenuPosition?.openLeft ? '-rotate-90' : 'rotate-90') : ''}`}
                            />
                          </div>
                        </button>
                      </Tooltip>

                      {expandedSubmenu === option.value &&
                        submenuPosition &&
                        createPortal(
                          <div
                            ref={submenuRef}
                            className="ed-dropdown absolute w-64 themed-border-radius-sm border border-themed-primary overflow-hidden z-[251] bg-themed-secondary animate-[dropdownSlideDown_0.15s_cubic-bezier(0.16,1,0.3,1)]"
                            style={{
                              top: submenuPosition.top,
                              left: submenuPosition.left
                            }}
                          >
                            {option.submenuTitle && (
                              <div className="px-3 py-2 text-xs font-semibold border-b border-themed-primary text-themed-secondary bg-themed-tertiary">
                                {option.submenuTitle}
                              </div>
                            )}
                            <CustomScrollbar maxHeight="240px" variant="float">
                              <div>
                                {option.submenu.map((subItem) => {
                                  const isSubSelected =
                                    value === `${option.value}:${subItem.value}`;
                                  return (
                                    <button
                                      key={subItem.value}
                                      type="button"
                                      onClick={() =>
                                        handleSelect(`${option.value}:${subItem.value}`)
                                      }
                                      className={`ed-submenu-option w-full flex items-center gap-2.5 px-3 py-2.5 text-sm ${
                                        isSubSelected
                                          ? 'ed-submenu-selected bg-[var(--theme-primary)] text-themed-button'
                                          : 'bg-transparent text-themed-primary'
                                      }`}
                                    >
                                      {(subItem.colorIndex || subItem.color) && (
                                        <div
                                          className="w-2.5 h-2.5 rounded-full flex-shrink-0"
                                          style={{
                                            backgroundColor: subItem.colorIndex
                                              ? getEventColorVar(subItem.colorIndex)
                                              : subItem.color
                                          }}
                                        />
                                      )}
                                      <div className="flex-1 min-w-0 text-left">
                                        <div className="flex items-center gap-1.5">
                                          <span className="font-medium truncate">
                                            {subItem.label}
                                          </span>
                                          {subItem.badge && (
                                            <span
                                              className="px-1.5 py-0.5 text-[10px] rounded-full font-medium"
                                              style={{
                                                backgroundColor: isSubSelected
                                                  ? 'rgba(255,255,255,0.2)'
                                                  : `${(subItem.badgeColor || 'var(--theme-status-success)').replace(')', '-muted)')}`,
                                                color: isSubSelected
                                                  ? 'var(--theme-button-text)'
                                                  : subItem.badgeColor ||
                                                    'var(--theme-status-success)'
                                              }}
                                            >
                                              {subItem.badge}
                                            </span>
                                          )}
                                        </div>
                                        {subItem.description && (
                                          <div
                                            className={`text-xs truncate ${isSubSelected ? 'text-white/70' : 'text-themed-muted'}`}
                                          >
                                            {subItem.description}
                                          </div>
                                        )}
                                      </div>
                                      {isSubSelected && (
                                        <Check
                                          size={14}
                                          className="flex-shrink-0 text-themed-button"
                                        />
                                      )}
                                    </button>
                                  );
                                })}
                              </div>
                            </CustomScrollbar>
                          </div>,
                          document.body
                        )}
                    </React.Fragment>
                  ) : (
                    <React.Fragment key={option.value}>
                      {(() => {
                        const isSelected = option.value === value;
                        const buttonContent = (
                          <button
                            type="button"
                            onClick={() => !option.disabled && handleSelect(option.value)}
                            disabled={option.disabled}
                            className={`ed-option w-full ${compactMode ? 'px-2 py-1 text-xs' : 'px-3 py-2.5 text-sm'} text-left ${option.disabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'} ${isSelected ? 'ed-option-selected' : ''}`}
                          >
                            <div className={`flex items-start ${compactMode ? 'gap-2' : 'gap-3'}`}>
                              {!cleanStyle && option.icon && (
                                <option.icon
                                  className={`flex-shrink-0 mt-0.5 ${isSelected ? 'text-[var(--theme-selected-text)]' : 'text-themed-secondary'}`}
                                  size={compactMode ? 12 : 16}
                                />
                              )}
                              <div className="flex flex-col flex-1 min-w-0">
                                <span
                                  className={`font-medium truncate ${isSelected ? 'text-[var(--theme-selected-text)]' : 'text-themed-primary'}`}
                                >
                                  {option.label}
                                </span>
                                {option.description && (
                                  <span className="text-xs mt-0.5 leading-relaxed text-themed-secondary">
                                    {option.description}
                                  </span>
                                )}
                              </div>
                              {option.rightLabel && (
                                <span
                                  className={`flex-shrink-0 text-xs font-medium ${isSelected ? 'text-[var(--theme-selected-text)]' : 'text-themed-secondary'}`}
                                >
                                  {option.rightLabel}
                                </span>
                              )}
                              {!cleanStyle && isSelected && (
                                <Check
                                  size={compactMode ? 12 : 16}
                                  className="flex-shrink-0 mt-0.5 text-[var(--theme-selected-text)]"
                                />
                              )}
                            </div>
                          </button>
                        );
                        return option.tooltip ? (
                          <Tooltip content={option.tooltip} className="w-full">
                            {buttonContent}
                          </Tooltip>
                        ) : (
                          buttonContent
                        );
                      })()}
                    </React.Fragment>
                  )
                )}
              </div>
            </CustomScrollbar>

            {footerNote && (
              <div className="px-3 py-2.5 text-xs border-t border-themed-primary flex items-start gap-2 text-themed-secondary bg-themed-tertiary">
                {FooterIcon && (
                  <FooterIcon className="flex-shrink-0 mt-0.5 text-themed-warning" size={14} />
                )}
                <span className="leading-relaxed">{footerNote}</span>
              </div>
            )}
          </div>,
          document.body
        )}
    </div>
  );
};
