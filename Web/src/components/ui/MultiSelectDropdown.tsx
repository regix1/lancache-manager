import React, {
  useState,
  useLayoutEffect,
  useEffect,
  useRef,
  useMemo,
  useCallback,
  memo
} from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, Check } from 'lucide-react';
import { CustomScrollbar } from './CustomScrollbar';
import { useTranslation } from 'react-i18next';
import { useAnchorFollow, type AnchorMoveHandler } from '@hooks/useAnchorFollow';

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
          className={`msd-checkbox flex-shrink-0 ${compact ? 'w-4 h-4' : 'w-5 h-5'} rounded-md flex items-center justify-center mt-0.5 ${
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
  compactMode = false
}) => {
  const { t } = useTranslation();
  const [isOpen, setIsOpen] = useState(false);
  // `top`/`left` are DOCUMENT coordinates: the menu is absolutely positioned in a
  // body portal so that a scroll carries it and its trigger together natively
  // rather than having JavaScript chase the trigger every frame (see useAnchorFollow).
  const [dropdownStyle, setDropdownStyle] = useState<{
    top: number;
    left: number;
    animation: string;
  }>({ top: 0, left: 0, animation: '' });
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  const valuesSet = useMemo(() => new Set(values), [values]);
  const selectedCount = valuesSet.size;

  const displayLabel = useMemo(() => {
    const defaultPlaceholder = placeholder || t('ui.multiSelect.selectOptions');
    if (selectedCount === 0) return defaultPlaceholder;
    if (selectedCount === 1) {
      const opt = options.find((o) => valuesSet.has(o.value));
      return opt?.label || defaultPlaceholder;
    }
    if (selectedCount === options.length) return t('ui.multiSelect.allSelected');
    return `${selectedCount} ${t('ui.multiSelect.selected')}`;
  }, [selectedCount, options, valuesSet, placeholder, t]);

  const updatePosition = useCallback(() => {
    if (!buttonRef.current) return;

    const rect = buttonRef.current.getBoundingClientRect();
    // offsetWidth/offsetHeight, NOT getBoundingClientRect: the entrance keyframes
    // scale the menu (`scale(0.97)`), so a bounding rect measured mid-animation
    // reports that scaled size - and an upward menu, placed by subtracting its
    // height from the trigger's top, would land on top of the button.
    const menuEl = dropdownRef.current;
    const dropdownHeight = menuEl?.offsetHeight || 300;
    const spaceBelow = window.innerHeight - rect.bottom;
    const openUpward = spaceBelow < dropdownHeight && rect.top > spaceBelow;

    const dropdownWidthPx = menuEl?.offsetWidth || 200;
    let left = rect.left;

    if (alignRight) {
      const pos = rect.right - dropdownWidthPx;
      if (pos >= 8) left = pos;
    } else if (rect.left + dropdownWidthPx > window.innerWidth - 8) {
      left =
        rect.right - dropdownWidthPx >= 8
          ? rect.right - dropdownWidthPx
          : rect.left + (rect.width - dropdownWidthPx) / 2;
    }

    // Both directions anchor by `top`: `bottom` would be measured from the bottom of
    // the document once the menu is absolutely positioned, not the viewport.
    const top = openUpward ? rect.top - 4 - dropdownHeight : rect.bottom + 4;

    setDropdownStyle({
      top: top + window.scrollY,
      left: left + window.scrollX,
      animation: openUpward
        ? 'msdFadeInUp 0.18s cubic-bezier(0.16, 1, 0.3, 1) forwards'
        : 'msdFadeInDown 0.18s cubic-bezier(0.16, 1, 0.3, 1) forwards'
    });
  }, [alignRight]);

  // Calculate position before paint and when opening
  useLayoutEffect(() => {
    if (!isOpen) return;
    updatePosition();
  }, [isOpen, updatePosition]);

  // Combined event listeners
  useEffect(() => {
    if (!isOpen) return;

    const isEventInside = (event: Event) => {
      const path = (event as Event & { composedPath?: () => EventTarget[] }).composedPath?.() || [];
      if (dropdownRef.current && path.includes(dropdownRef.current)) return true;
      if (buttonRef.current && path.includes(buttonRef.current)) return true;
      return false;
    };

    const handlePointerDown = (e: PointerEvent) => {
      if (!isEventInside(e)) {
        setIsOpen(false);
      }
    };

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };

    document.addEventListener('pointerdown', handlePointerDown);
    document.addEventListener('keydown', handleEscape);
    return () => {
      document.removeEventListener('pointerdown', handlePointerDown);
      document.removeEventListener('keydown', handleEscape);
    };
  }, [isOpen]);

  // No scroll listener: the menu is positioned on the page, so a scroll moves it and
  // its trigger together with no JavaScript at all. Reflows (and window resizes) are
  // what actually move the trigger, and useAnchorFollow reports those.
  const handleAnchorMove: AnchorMoveHandler = useCallback((): void => {
    updatePosition();
  }, [updatePosition]);

  /** Nothing left to anchor to once the trigger is scrolled off screen. */
  const handleAnchorLost = useCallback((): void => {
    setIsOpen(false);
  }, []);

  useAnchorFollow({
    enabled: isOpen,
    anchorRef: buttonRef,
    onAnchorMove: handleAnchorMove,
    onAnchorLost: handleAnchorLost
  });

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
        <span className="truncate flex-1">{displayLabel}</span>
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

      {isOpen &&
        createPortal(
          <div
            ref={dropdownRef}
            className={`msd-dropdown absolute z-[250] ${dropdownWidth || ''} themed-border-radius-sm overflow-hidden bg-themed-secondary border border-themed-primary`}
            style={{
              top: dropdownStyle.top,
              left: dropdownStyle.left,
              animation: dropdownStyle.animation,
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

            <CustomScrollbar maxHeight="280px" variant="float" radius="none">
              <div
                className="bg-themed-secondary"
                style={{ overscrollBehavior: 'contain' }}
                onWheel={(event) => event.stopPropagation()}
                onTouchMove={(event) => event.stopPropagation()}
              >
                {options.map((option, i) => (
                  <OptionItem
                    key={option.value}
                    option={option}
                    isSelected={valuesSet.has(option.value)}
                    isDisabled={
                      option.disabled ||
                      (valuesSet.has(option.value) && !canDeselect) ||
                      (!valuesSet.has(option.value) && !canSelect)
                    }
                    isLast={i === options.length - 1}
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
