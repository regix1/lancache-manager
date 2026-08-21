import React, {
  useState,
  useEffect,
  useId,
  useMemo,
  useRef,
  useLayoutEffect,
  useCallback
} from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, ChevronRight, Check } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { CustomScrollbar } from './CustomScrollbar';
import { Tooltip } from './Tooltip';
import { getEventColorVar, themeColorVar } from '@utils/eventColors';
import { clampToViewport, MENU_GUTTER_PX } from '@utils/viewportClamp';
import { useAnchoredPanel, type PanelPlacement, type PanelSpace } from '@hooks/useAnchoredPanel';
import { useTextTruncation } from '@hooks/useTextTruncation';

interface SubmenuGeometry {
  top: number;
  left: number;
  openLeft: boolean;
}

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

/**
 * Whether a row can be chosen outright. A divider is a heading, a disabled row is refusing, and a
 * row with a submenu opens that submenu rather than settling on a value, so the keyboard walks
 * past all three.
 */
function isSelectableOption(option: DropdownOption): boolean {
  return (
    option.value !== 'divider' && !option.disabled && !(option.submenu && option.submenu.length > 0)
  );
}

export interface SubmenuOption {
  value: string;
  label: string;
  description?: string;
  color?: string;
  colorIndex?: number;
  badge?: string;
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
  /**
   * Puts a filter box at the top of the menu. Worth it once the list is long enough that
   * scrolling it is worse than typing; a short menu reads faster without one.
   */
  searchable?: boolean;
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
  size = 'md',
  searchable = false
}) => {
  const { t } = useTranslation();
  const [isOpen, setIsOpen] = useState(false);
  const [searchTerm, setSearchTerm] = useState('');
  /** The row the arrow keys are on, so Enter takes what the reader is looking at. */
  const [activeValue, setActiveValue] = useState<string | null>(null);
  const [dropdownStyle, setDropdownStyle] = useState<{ animation: string }>({ animation: '' });
  const [expandedSubmenu, setExpandedSubmenu] = useState<string | null>(null);
  const [submenuPosition, setSubmenuPosition] = useState<SubmenuGeometry | null>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const submenuRef = useRef<HTMLDivElement>(null);
  const searchInputRef = useRef<HTMLInputElement>(null);
  /** The row the arrow keys are on, held so it can be scrolled back into view as they move. */
  const activeRowRef = useRef<HTMLButtonElement | null>(null);
  /** The option row the open submenu hangs off, so it can be re-measured as the menu moves. */
  const submenuTriggerRef = useRef<HTMLButtonElement | null>(null);
  /** Side the menu is currently opened on (null while closed). Feeds the flip hysteresis. */
  const upwardRef = useRef<boolean | null>(null);
  // The menu is portalled to <body>, so it is not a DOM descendant of the trigger and a reader has
  // no structural way to connect the two. The ids below are what carry that relationship instead:
  // aria-controls names the list, aria-activedescendant names the row the arrow keys are on while
  // focus stays on either the trigger or the search box. React's generated ids contain colons,
  // which are legal in an id attribute but not in a CSS selector, so they are stripped the same
  // way ContentPathRow does.
  const dropdownId = useId().replace(/:/g, '');
  const listboxId = `${dropdownId}-listbox`;
  const optionId = (value: string): string => `${dropdownId}-option-${encodeURIComponent(value)}`;

  const selectedOption =
    options.find((opt) => opt.value === value) ||
    (value.includes(':')
      ? options.find((opt) => opt.submenu && value.startsWith(opt.value + ':'))
      : undefined);
  const selectedValue = selectedOption?.value ?? null;

  /**
   * What the menu shows for the current filter, and the options untouched when there is none.
   * A search flattens the submenus: an entry that only exists inside "Europe" cannot be reached
   * by a search that can just match the region row, so a matching child is offered directly. It
   * still selects as "Europe:Europe/Berlin", exactly as it would have from the submenu, so a
   * caller reads one shape whichever way it was picked.
   */
  const visibleOptions = useMemo((): DropdownOption[] => {
    const term = searchable ? searchTerm.trim().toLowerCase() : '';
    if (term.length === 0) return options;
    const matches = (...fields: (string | undefined)[]): boolean =>
      fields.some((field) => field !== undefined && field.toLowerCase().includes(term));
    const found: DropdownOption[] = [];
    for (const option of options) {
      if (option.value === 'divider') continue;
      if (!option.submenu || option.submenu.length === 0) {
        if (matches(option.label, option.description, option.value)) found.push(option);
        continue;
      }
      // A region whose own name matches offers everything under it; otherwise only the
      // entries that match themselves.
      const wholeGroup = matches(option.label, option.description);
      for (const entry of option.submenu) {
        if (!wholeGroup && !matches(entry.label, entry.description, entry.value)) continue;
        found.push({
          value: `${option.value}:${entry.value}`,
          label: entry.label,
          description: option.label
        });
      }
    }
    return found;
  }, [options, searchTerm, searchable]);

  /** The rows the arrow keys can land on, in the order they are drawn. */
  const selectableValues = useMemo(
    (): string[] => visibleOptions.filter(isSelectableOption).map((option) => option.value),
    [visibleOptions]
  );

  /**
   * Where each row sits in the list and how long the list is. A filter rewrites both, and the count
   * is the only way a reader learns that typing another letter cut the list from twelve rows to one:
   * the rows scroll past silently otherwise. Dividers are headings rather than choices, so they are
   * not counted.
   */
  const optionPositions = useMemo((): Map<string, number> => {
    const positions = new Map<string, number>();
    for (const option of visibleOptions) {
      if (option.value === 'divider') continue;
      positions.set(option.value, positions.size + 1);
    }
    return positions;
  }, [visibleOptions]);

  useEffect(() => {
    if (!isOpen) {
      // Submenu closes immediately; the main menu keeps its position through the
      // exit animation and is cleared once fully unmounted (see the effect below).
      setExpandedSubmenu(null);
      setSubmenuPosition(null);
      submenuTriggerRef.current = null;
      // A filter only ever describes the menu that is open, so the next opening starts whole.
      setSearchTerm('');
      setActiveValue(null);
    }
  }, [isOpen]);

  // Every composite focus owner names a real row while its list is open. Search keeps its
  // established first-match fallback; a trigger-owned list starts on the selected row so arrow
  // movement is preview-only until Enter, Space, or a click commits it.
  useEffect(() => {
    if (!isOpen) return;
    const fallback =
      !searchable && selectedValue !== null && selectableValues.includes(selectedValue)
        ? selectedValue
        : (selectableValues[0] ?? null);
    setActiveValue((current) =>
      current !== null && selectableValues.includes(current) ? current : fallback
    );
  }, [isOpen, searchable, selectableValues, selectedValue]);

  // A row arrowed past the bottom of the panel has to be brought back, and the panel is its own
  // scroll box rather than the page's, so the row asks its nearest scrolling ancestor.
  useEffect(() => {
    if (activeValue === null) return;
    activeRowRef.current?.scrollIntoView({ block: 'nearest' });
  }, [activeValue]);

  /**
   * Resolves where the menu sits. This is not the shared below/above placement: the
   * side is picked by `chooseUpward`, which is biased toward staying put so a menu
   * being carried by a reflow never oscillates, and before the menu is in the DOM the
   * decision runs off an estimated height and width instead of a zero one.
   */
  const place = useCallback(
    (space: PanelSpace): PanelPlacement => {
      const { anchor, viewportWidth, viewportHeight, gutter } = space;
      // The measured box is the layout size and ignores transforms, which matters
      // because the entrance keyframes scale the menu (`scale(0.98)`): an upward menu
      // placed from a height ~2% short parks right on top of the button.
      const parsedMaxHeight = maxHeight && maxHeight.endsWith('px') ? parseInt(maxHeight, 10) : 300;
      const estimatedContentHeight = compactMode
        ? Math.min(parsedMaxHeight, options.length * 24 + 8)
        : parsedMaxHeight;
      const menuHeight = space.panelHeight > 0 ? space.panelHeight : estimatedContentHeight + 50;
      const menuWidth =
        space.panelWidth > 0
          ? space.panelWidth
          : resolveDropdownWidthToPx(dropdownWidth, anchor.width);

      const spaceBelow = viewportHeight - anchor.bottom - MENU_GAP_PX;
      const spaceAbove = anchor.top - MENU_GAP_PX;
      // The side the menu is on now is the hysteresis's only memory, and a ref is what
      // lets it be read on the next placement without giving this callback a new identity.
      const upward = chooseUpward(upwardRef.current, spaceAbove, spaceBelow, menuHeight);
      upwardRef.current = upward;

      const desiredLeft = alignRight ? anchor.right - menuWidth : anchor.left;
      const left = clampToViewport(desiredLeft, menuWidth, viewportWidth, gutter);

      // A single top edge for both directions: an upward menu hangs its bottom edge
      // off the trigger's top. Anchoring by `bottom` instead would be measured from
      // the bottom of the *document* once the menu is absolutely positioned.
      const desiredTop = upward
        ? anchor.top - MENU_GAP_PX - menuHeight
        : anchor.bottom + MENU_GAP_PX;
      // The side is already chosen above, so this only pulls a menu that is taller
      // than the room on that side back onto the screen. It cannot change the side,
      // which is what keeps chooseUpward's hysteresis intact.
      const top = clampToViewport(desiredTop, menuHeight, viewportHeight, gutter);

      return { top, left, openUpward: upward };
    },
    [alignRight, dropdownWidth, maxHeight, compactMode, options.length]
  );

  const closeDropdown = useCallback((): void => setIsOpen(false), []);

  const {
    present,
    closing,
    position: dropdownPosition,
    anchorWidth: triggerWidth
  } = useAnchoredPanel({
    open: isOpen,
    anchorRef: buttonRef,
    panelRef: dropdownRef,
    onClose: closeDropdown,
    gutter: MENU_GUTTER_PX,
    place
  });

  /**
   * Writes the entrance keyframe once per open, from the direction the hook has already
   * settled on in this same commit. A flip later on updates `upwardRef` for the exit
   * mirror but must not replay the entrance under the reader's cursor.
   */
  useLayoutEffect(() => {
    if (!present) {
      upwardRef.current = null;
      setDropdownStyle({ animation: '' });
      return;
    }
    setDropdownStyle({
      animation: `${upwardRef.current ? 'dropdownSlideUp' : 'dropdownSlideDown'} 0.15s cubic-bezier(0.16, 1, 0.3, 1)`
    });
  }, [present]);

  /** Re-measures the open submenu against its (possibly moved) option row. */
  const syncSubmenuPosition = useCallback((): void => {
    const trigger = submenuTriggerRef.current;
    if (!trigger) return;

    const next = computeSubmenuGeometry(trigger.getBoundingClientRect());
    setSubmenuPosition((prev) => (prev && isSameSubmenuPosition(prev, next) ? prev : next));
  }, []);

  // The parent menu's coordinates are state-driven. Measure the submenu row only
  // after React has committed those coordinates, while still correcting the
  // separately portalled submenu before the browser paints.
  useLayoutEffect(() => {
    if (!expandedSubmenu || !present) return;
    syncSubmenuPosition();
  }, [expandedSubmenu, present, dropdownPosition, syncSubmenuPosition]);

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

    // Click-outside only. Escape belongs to the shared hook, and there is still no
    // scroll handler: the menu follows its trigger and dismisses itself if the trigger
    // leaves the viewport. Closing on `scroll` was also actively harmful here -
    // inserting the notification bar above the viewport makes the browser's scroll
    // anchoring adjust scrollTop, which fires a scroll event and used to close an
    // open menu even though nothing had visibly moved.
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen]);

  const openDropdown = useCallback((): void => {
    if (disabled) return;
    setIsOpen(true);
  }, [disabled]);

  const handleSelect = useCallback(
    (optionValue: string) => {
      onChange(optionValue);
      setIsOpen(false);
    },
    [onChange]
  );

  const moveActive = useCallback(
    (step: 1 | -1): void => {
      if (selectableValues.length === 0) return;
      setActiveValue((current) => {
        const at =
          current !== null
            ? selectableValues.indexOf(current)
            : searchable
              ? -1
              : selectedValue !== null
                ? selectableValues.indexOf(selectedValue)
                : -1;
        // Stops at each end rather than wrapping: a four-hundred-row list wrapping from the top
        // to the bottom loses the reader's place entirely.
        const next = Math.min(Math.max(at + step, 0), selectableValues.length - 1);
        return selectableValues[next];
      });
    },
    [searchable, selectableValues, selectedValue]
  );

  /**
   * The keyboard path through a filtered menu. Focus stays in the box being typed into, so the
   * arrows move a highlighted row rather than focus itself, and Enter takes that row. Without it
   * a list long enough to want a filter can only be reached by typing until one row is left and
   * hoping it is the right one.
   */
  const handleSearchKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLInputElement>): void => {
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        if (selectableValues.length === 0) return;
        event.preventDefault();
        moveActive(event.key === 'ArrowDown' ? 1 : -1);
        return;
      }
      if (event.key !== 'Enter') return;
      const chosen =
        activeValue !== null && selectableValues.includes(activeValue)
          ? activeValue
          : selectableValues[0];
      if (chosen === undefined) return;
      event.preventDefault();
      handleSelect(chosen);
    },
    [activeValue, handleSelect, moveActive, selectableValues]
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

  const handleTriggerKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLButtonElement>): void => {
      if (searchable || disabled) return;

      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        if (selectableValues.length === 0) return;
        event.preventDefault();
        if (!isOpen) openDropdown();
        moveActive(event.key === 'ArrowDown' ? 1 : -1);
        return;
      }

      if (!isOpen || (event.key !== 'Enter' && event.key !== ' ')) return;
      const chosen =
        activeValue !== null && selectableValues.includes(activeValue)
          ? activeValue
          : selectableValues[0];
      if (chosen === undefined) return;
      // Prevent the button's synthesized click from immediately toggling the menu after selection.
      event.preventDefault();
      handleSelect(chosen);
    },
    [
      activeValue,
      disabled,
      handleSelect,
      isOpen,
      moveActive,
      openDropdown,
      searchable,
      selectableValues
    ]
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
  // The trigger is a fixed-width control and the label is clipped to fit it, so the label
  // is measured to decide whether a hover box has anything to add. A label that fits reads
  // the same in the tooltip as it does on the button; a clipped one has no other way to be
  // read. Re-measured on every width change, since the same name fits at one window size
  // and not at the next.
  const { ref: labelRef, isTruncated: isLabelTruncated } = useTextTruncation(displayLabel);
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
            openDropdown();
          } else {
            setIsOpen(false);
          }
        }}
        onKeyDown={handleTriggerKeyDown}
        onTouchEnd={(e) => {
          e.stopPropagation();
        }}
        disabled={disabled}
        role="combobox"
        aria-label={resolvedAriaLabel}
        aria-haspopup="listbox"
        aria-expanded={isOpen}
        aria-controls={listboxId}
        aria-activedescendant={
          !searchable && isOpen && activeValue !== null && optionPositions.has(activeValue)
            ? optionId(activeValue)
            : undefined
        }
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
            // The Tooltip stays mounted whether or not it has anything to say. Adding and
            // removing it would move the label between a wrapped div and a direct flex item,
            // and a flex item's automatic minimum width stops it shrinking, so the label
            // would measure as untruncated the moment the tooltip came off and then flicker
            // between the two states.
            <Tooltip
              content={isLabelTruncated ? displayLabel : null}
              position="top"
              className="min-w-0"
            >
              <span ref={labelRef} className={compactMode ? 'block font-medium' : 'block truncate'}>
                {displayLabel}
              </span>
            </Tooltip>
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
                  ? { width: triggerWidth }
                  : {}),
              ...(!dropdownWidth ? { minWidth: triggerWidth } : {}),
              animation: menuAnimation,
              pointerEvents: closing ? 'none' : undefined
            }}
          >
            {dropdownTitle && (
              <div className="px-3 py-2 text-sm font-medium border-b border-themed-primary bg-themed-secondary text-themed-secondary">
                {dropdownTitle}
              </div>
            )}

            {searchable && (
              <div className="px-2 py-2 border-b border-themed-primary bg-themed-secondary">
                {/* The box that holds focus is the one that owns the list: the arrow keys move a
                    highlight through rows the caret never enters, so the row Enter would take is
                    named here by id rather than by focus. */}
                <input
                  ref={searchInputRef}
                  autoFocus
                  type="text"
                  role="searchbox"
                  value={searchTerm}
                  onChange={(event) => setSearchTerm(event.target.value)}
                  onKeyDown={handleSearchKeyDown}
                  placeholder={t('common.search')}
                  aria-label={t('common.search')}
                  aria-autocomplete="list"
                  aria-controls={listboxId}
                  aria-activedescendant={
                    activeValue !== null && optionPositions.has(activeValue)
                      ? optionId(activeValue)
                      : undefined
                  }
                  className="themed-input control-h-md w-full px-3 text-sm"
                />
              </div>
            )}

            {searchable && (
              <div role="status" aria-live="polite" aria-atomic="true" className="sr-only">
                {visibleOptions.length === 0
                  ? t('ui.dropdown.noMatches')
                  : t('ui.pagination.itemRange', {
                      start: 1,
                      end: visibleOptions.length,
                      total: visibleOptions.length,
                      label: t('ui.pagination.items')
                    })}
              </div>
            )}

            <CustomScrollbar
              maxHeight={cleanStyle ? 'none' : maxHeight || '280px'}
              variant="float"
              className="!rounded-none"
            >
              {/* No vertical padding: the first/last option's highlight must reach the
                  panel edge, where the rounded overflow clip finishes the corners. */}
              <div id={listboxId} role="listbox" aria-label={resolvedAriaLabel}>
                {searchable && visibleOptions.length === 0 && (
                  <div className="px-3 py-3 text-sm text-themed-muted">
                    {t('ui.dropdown.noMatches')}
                  </div>
                )}
                {visibleOptions.map((option) =>
                  option.value === 'divider' ? (
                    <div
                      key={option.value}
                      role="separator"
                      className="px-3 py-2 text-xs font-medium border-t border-themed-primary mt-1 mb-1 truncate text-themed-muted bg-themed-tertiary"
                    >
                      {option.label}
                    </div>
                  ) : option.submenu && option.submenu.length > 0 ? (
                    <React.Fragment key={option.value}>
                      <button
                        type="button"
                        id={optionId(option.value)}
                        role="option"
                        aria-selected={value.startsWith(option.value + ':')}
                        aria-expanded={expandedSubmenu === option.value}
                        aria-setsize={optionPositions.size}
                        aria-posinset={optionPositions.get(option.value)}
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
                            <CustomScrollbar maxHeight="240px" variant="float" radius="none">
                              <div role="listbox" aria-label={option.submenuTitle || option.label}>
                                {option.submenu.map((subItem, subIndex) => {
                                  const isSubSelected =
                                    value === `${option.value}:${subItem.value}`;
                                  return (
                                    <button
                                      key={subItem.value}
                                      type="button"
                                      id={optionId(`${option.value}:${subItem.value}`)}
                                      role="option"
                                      aria-selected={isSubSelected}
                                      aria-setsize={option.submenu?.length}
                                      aria-posinset={subIndex + 1}
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
                                          <span className="block min-w-0 font-medium truncate">
                                            {subItem.label}
                                          </span>
                                          {subItem.badge && (
                                            <span
                                              className="px-1.5 py-0.5 text-[10px] rounded-full font-medium"
                                              style={{
                                                backgroundColor: isSubSelected
                                                  ? 'rgba(255,255,255,0.2)'
                                                  : themeColorVar('--theme-success', 'muted'),
                                                color: isSubSelected
                                                  ? 'var(--theme-button-text)'
                                                  : themeColorVar('--theme-success')
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
                        // The row the arrow keys are on. The selected row's own fill wins where
                        // the two land together, which is the state they start in.
                        const isActive = option.value === activeValue;
                        const buttonContent = (
                          <button
                            type="button"
                            ref={isActive ? activeRowRef : undefined}
                            id={optionId(option.value)}
                            role="option"
                            aria-selected={isSelected}
                            aria-disabled={option.disabled}
                            aria-setsize={optionPositions.size}
                            aria-posinset={optionPositions.get(option.value)}
                            onClick={() => !option.disabled && handleSelect(option.value)}
                            disabled={option.disabled}
                            className={`ed-option w-full ${compactMode ? 'px-2 py-1 text-xs' : 'px-3 py-2.5 text-sm'} text-left ${option.disabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'} ${isSelected ? 'ed-option-selected' : isActive ? 'bg-themed-hover' : ''}`}
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
                        // Only an explicit `tooltip` earns a hover box. Falling back to the label
                        // put the row's own heading in a box on top of the row, and the menu sits
                        // above the tooltip layer, so every row but the first hid it anyway.
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
