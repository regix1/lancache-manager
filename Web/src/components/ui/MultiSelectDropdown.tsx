import React, { useState, useEffect, useRef, useMemo, useCallback, memo } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, Check } from 'lucide-react';
import { CustomScrollbar } from './CustomScrollbar';
import { Tooltip } from './Tooltip';
import { useTranslation } from 'react-i18next';
import { useAnchoredPanel, type PanelPlacement, type PanelSpace } from '@hooks/useAnchoredPanel';
import { clampToViewport, MENU_GUTTER_PX } from '@utils/viewportClamp';

const MENU_GAP_PX = 4;
/** Pre-measurement guesses, used only on the pass before the menu is in the DOM. */
const MENU_HEIGHT_GUESS_PX = 300;
const MENU_WIDTH_GUESS_PX = 200;

interface IconComponentProps {
  size?: number;
  className?: string;
  style?: React.CSSProperties;
}

export interface MultiSelectOption {
  value: string;
  label: string;
  description?: string;
  icon?: React.ComponentType<IconComponentProps>;
  disabled?: boolean;
}

interface MultiSelectDropdownProps {
  options: MultiSelectOption[];
  values: string[];
  onChange: (values: string[]) => void;
  placeholder?: string;
  className?: string;
  disabled?: boolean;
  dropdownWidth?: string;
  alignRight?: boolean;
  title?: string;
  minSelections?: number;
  maxSelections?: number;
  compactMode?: boolean;
  /** Puts a filter box above the option list. Off by default so existing menus are unchanged. */
  searchable?: boolean;
}

// Memoized option component
interface OptionItemProps {
  option: MultiSelectOption;
  isSelected: boolean;
  isDisabled: boolean;
  isLast: boolean;
  onToggle: (value: string) => void;
  compact?: boolean;
}

const OptionItem = memo<OptionItemProps>(
  ({ option, isSelected, isDisabled, isLast, onToggle, compact }) => {
    const Icon = option.icon;

    return (
      <button
        type="button"
        onClick={() => !isDisabled && onToggle(option.value)}
        disabled={isDisabled}
        className={`
        msd-option w-full text-left flex items-start ${compact ? 'gap-2 px-2 py-1' : 'gap-3 px-4 py-3.5'} bg-themed-secondary
        ${isSelected ? 'msd-option-selected' : ''}
        ${isDisabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'}
        ${!isLast ? 'border-b border-themed-secondary' : ''}
      `}
      >
        <div className="msd-accent absolute left-0 top-0 bottom-0 w-0.5 bg-[var(--theme-primary)]" />

        <div
          className={`msd-checkbox flex-shrink-0 ${compact ? 'w-4 h-4' : 'w-5 h-5'} flex items-center justify-center mt-0.5 ${
            isSelected
              ? 'msd-checkbox-selected bg-[var(--theme-primary)] border-none shadow-[0_2px_4px_var(--theme-primary-strong)]'
              : 'bg-transparent border-2 border-themed-primary shadow-none'
          }`}
        >
          <Check
            className={`msd-checkbox-inner ${compact ? 'w-2.5 h-2.5' : 'w-3.5 h-3.5'} text-white`}
            strokeWidth={3}
          />
        </div>

        {Icon && (
          <div
            className={`flex-shrink-0 mt-0.5 ${isSelected ? 'text-[var(--theme-selected-text)]' : 'text-themed-muted'}`}
          >
            <Icon size={compact ? 14 : 18} />
          </div>
        )}

        <div className="flex-1 min-w-0">
          <div
            className={`${compact ? 'text-xs' : 'text-sm'} font-medium leading-tight text-themed-primary`}
          >
            {option.label}
          </div>
          {option.description && (
            <div className="msd-option-desc text-xs leading-[1.4] mt-1 text-themed-muted">
              {option.description}
            </div>
          )}
        </div>
      </button>
    );
  }
);

OptionItem.displayName = 'OptionItem';

export const MultiSelectDropdown: React.FC<MultiSelectDropdownProps> = ({
  options,
  values,
  onChange,
  placeholder,
  className = '',
  disabled = false,
  dropdownWidth,
  alignRight = false,
  title,
  minSelections = 1,
  maxSelections,
  compactMode = false,
  searchable = false
}) => {
  const { t } = useTranslation();
  const [isOpen, setIsOpen] = useState(false);
  const [searchTerm, setSearchTerm] = useState('');
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const searchInputRef = useRef<HTMLInputElement>(null);

  const valuesSet = useMemo(() => new Set(values), [values]);
  const selectedCount = valuesSet.size;

  // The trigger label and the "all selected" test read the full list; only the rendered
  // rows narrow, so a filtered menu never misreports what is selected.
  const filteredOptions = useMemo(() => {
    const term = searchable ? searchTerm.trim().toLowerCase() : '';
    if (!term) return options;
    return options.filter(
      (option) =>
        option.label.toLowerCase().includes(term) ||
        option.value.toLowerCase().includes(term) ||
        (option.description?.toLowerCase().includes(term) ?? false)
    );
  }, [options, searchTerm, searchable]);

  const displayLabel = useMemo(() => {
    const defaultPlaceholder = placeholder || t('ui.multiSelect.selectOptions');
    if (selectedCount === 0) return defaultPlaceholder;
    if (selectedCount === 1) {
      const opt = options.find((o) => valuesSet.has(o.value));
      return opt?.label || defaultPlaceholder;
    }
    if (selectedCount === options.length) return t('ui.multiSelect.allSelected');
    // The trigger already carries a count badge, so repeating the number here printed it
    // twice ("8 selected" beside an 8). Every caller passes a placeholder that names what is
    // being picked, which is more useful next to the badge than the word on its own.
    return defaultPlaceholder;
  }, [selectedCount, options, valuesSet, placeholder, t]);

  /**
   * A left-aligned menu that would overflow the right edge CENTRES on its trigger
   * rather than sliding to the gutter, which the shared below/above placement does not
   * do, so this menu supplies its own.
   */
  const place = useCallback(
    (space: PanelSpace): PanelPlacement => {
      const { anchor, viewportWidth, viewportHeight, gutter } = space;
      // offsetWidth/offsetHeight, NOT getBoundingClientRect: the entrance keyframes
      // scale the menu (`scale(0.97)`), so a bounding rect measured mid-animation
      // reports that scaled size - and an upward menu, placed by subtracting its
      // height from the trigger's top, would land on top of the button.
      const dropdownHeight = space.panelHeight || MENU_HEIGHT_GUESS_PX;
      const spaceBelow = viewportHeight - anchor.bottom;
      const openUpward = spaceBelow < dropdownHeight && anchor.top > spaceBelow;

      const dropdownWidthPx = space.panelWidth || MENU_WIDTH_GUESS_PX;
      let left = anchor.left;

      if (alignRight) {
        const pos = anchor.right - dropdownWidthPx;
        if (pos >= gutter) left = pos;
      } else if (anchor.left + dropdownWidthPx > viewportWidth - gutter) {
        left =
          anchor.right - dropdownWidthPx >= gutter
            ? anchor.right - dropdownWidthPx
            : anchor.left + (anchor.width - dropdownWidthPx) / 2;
      }

      // Both directions anchor by `top`: `bottom` would be measured from the bottom of
      // the document once the menu is absolutely positioned, not the viewport.
      const desiredTop = openUpward
        ? anchor.top - MENU_GAP_PX - dropdownHeight
        : anchor.bottom + MENU_GAP_PX;
      // Flipping alone only buys the trigger's distance from the edge, so a menu taller
      // than the room on the side it picked still hangs off the viewport.
      const top = clampToViewport(desiredTop, dropdownHeight, viewportHeight, gutter);

      return { top, left, openUpward };
    },
    [alignRight]
  );

  const closeDropdown = useCallback((): void => setIsOpen(false), []);

  const { present, closing, position } = useAnchoredPanel({
    open: isOpen,
    anchorRef: buttonRef,
    panelRef: dropdownRef,
    onClose: closeDropdown,
    gutter: MENU_GUTTER_PX,
    place
  });

  // A search only ever describes the menu that is open, and the input is at the end of the
  // document in a portal, so focusing it on open is the only way a keyboard reaches it.
  // Keyed on `present`, the same flag the portal is gated on: the input is mounted one
  // render after `isOpen` flips, so a focus call keyed on `isOpen` finds a null ref.
  useEffect(() => {
    if (!present) {
      setSearchTerm('');
      return;
    }
    if (searchable) {
      searchInputRef.current?.focus();
    }
  }, [present, searchable]);

  // Combined event listeners
  useEffect(() => {
    if (!isOpen) return;

    const isEventInside = (event: Event) => {
      const path = event.composedPath();
      if (dropdownRef.current && path.includes(dropdownRef.current)) return true;
      if (buttonRef.current && path.includes(buttonRef.current)) return true;
      return false;
    };

    const handlePointerDown = (e: PointerEvent) => {
      if (!isEventInside(e)) {
        setIsOpen(false);
      }
    };

    // No scroll listener: the menu is positioned on the page, so a scroll moves it and
    // its trigger together with no JavaScript at all. Escape belongs to the shared hook.
    document.addEventListener('pointerdown', handlePointerDown);
    return () => document.removeEventListener('pointerdown', handlePointerDown);
  }, [isOpen]);

  const handleToggle = useCallback(
    (optionValue: string) => {
      const isSelected = valuesSet.has(optionValue);
      if (isSelected) {
        if (selectedCount > minSelections) {
          onChange(values.filter((v) => v !== optionValue));
        }
      } else if (!maxSelections || selectedCount < maxSelections) {
        onChange([...values, optionValue]);
      }
    },
    [valuesSet, selectedCount, minSelections, maxSelections, onChange, values]
  );

  const handleSearchChange = useCallback((event: React.ChangeEvent<HTMLInputElement>): void => {
    setSearchTerm(event.target.value);
  }, []);

  const canDeselect = selectedCount > minSelections;
  const canSelect = !maxSelections || selectedCount < maxSelections;

  return (
    <div className={`relative ${className}`}>
      <button
        ref={buttonRef}
        type="button"
        onClick={() => !disabled && setIsOpen(!isOpen)}
        disabled={disabled}
        className={`msd-trigger w-full px-3 h-10 themed-border-radius-sm border text-left flex items-center justify-between gap-2 text-sm font-medium themed-card text-themed-primary ${
          isOpen ? 'msd-trigger-open border-themed-focus' : 'border-themed-primary'
        } ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}`}
      >
        <Tooltip content={displayLabel} position="top" className="flex-1 min-w-0">
          <span className="block truncate">{displayLabel}</span>
        </Tooltip>
        <div className="flex items-center gap-1.5 text-themed-muted">
          {selectedCount > 0 && (
            <span className="themed-badge badge-count badge-count-on-color font-semibold bg-[var(--theme-primary)] text-white">
              {selectedCount}
            </span>
          )}
          <ChevronDown
            className={`w-4 h-4 flex-shrink-0 transition-transform duration-200 ${isOpen ? 'rotate-180' : ''}`}
          />
        </div>
      </button>

      {/* Rendered while `present` (not just `isOpen`) so the exit animation plays. */}
      {present &&
        createPortal(
          <div
            ref={dropdownRef}
            className={`msd-dropdown absolute z-[250] ${dropdownWidth || ''} themed-border-radius-sm overflow-hidden bg-themed-secondary border border-themed-primary motion-reduce:animate-none ${
              closing
                ? position.openUpward
                  ? 'animate-[dropdownSlideOutUp_0.14s_ease-in_forwards]'
                  : 'animate-[dropdownSlideOutDown_0.14s_ease-in_forwards]'
                : position.openUpward
                  ? 'animate-[msdFadeInUp_0.18s_cubic-bezier(0.16,1,0.3,1)_forwards]'
                  : 'animate-[msdFadeInDown_0.18s_cubic-bezier(0.16,1,0.3,1)_forwards]'
            }`}
            style={{
              top: position.top,
              left: position.left,
              pointerEvents: closing ? 'none' : undefined,
              ...(!dropdownWidth ? { width: buttonRef.current?.getBoundingClientRect().width } : {})
            }}
            onPointerDown={(event) => event.stopPropagation()}
            onMouseDown={(event) => event.stopPropagation()}
          >
            {title && (
              <div className="px-4 py-3 text-xs font-semibold uppercase tracking-wider border-b border-themed-secondary text-themed-muted bg-themed-tertiary">
                {title}
              </div>
            )}

            {searchable && (
              <div className="px-2 py-2 border-b border-themed-secondary bg-themed-secondary">
                <input
                  ref={searchInputRef}
                  type="text"
                  value={searchTerm}
                  onChange={handleSearchChange}
                  placeholder={t('common.search')}
                  aria-label={t('common.search')}
                  className="themed-input control-h-md w-full px-3 text-sm"
                />
              </div>
            )}

            <CustomScrollbar maxHeight="280px" variant="float" radius="none">
              <div
                className="bg-themed-secondary"
                style={{ overscrollBehavior: 'contain' }}
                onWheel={(event) => event.stopPropagation()}
                onTouchMove={(event) => event.stopPropagation()}
              >
                {filteredOptions.length === 0
                  ? // Only a search can empty the list on purpose, so only a searching menu
                    // says so; without the box the panel stays exactly what it was.
                    searchable && (
                      <div className="px-4 py-3 text-sm text-themed-muted">
                        {t('ui.multiSelect.noMatches')}
                      </div>
                    )
                  : filteredOptions.map((option, i) => (
                      <OptionItem
                        key={option.value}
                        option={option}
                        isSelected={valuesSet.has(option.value)}
                        isDisabled={
                          option.disabled ||
                          (valuesSet.has(option.value) && !canDeselect) ||
                          (!valuesSet.has(option.value) && !canSelect)
                        }
                        isLast={i === filteredOptions.length - 1}
                        onToggle={handleToggle}
                        compact={compactMode}
                      />
                    ))}
              </div>
            </CustomScrollbar>

            {minSelections > 0 && (
              <div className="px-4 py-3 text-xs border-t border-themed-secondary flex items-center gap-2 text-themed-muted bg-themed-tertiary">
                <span>{t('ui.multiSelect.minimumSelections', { count: minSelections })}</span>
              </div>
            )}
          </div>,
          document.body
        )}
    </div>
  );
};
