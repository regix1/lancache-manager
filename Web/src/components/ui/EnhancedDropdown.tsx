import React, { useState, useEffect, useRef, useLayoutEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, ChevronRight, Check } from 'lucide-react';
import { CustomScrollbar } from './CustomScrollbar';
import { Tooltip } from './Tooltip';
import { getEventColorVar } from '@utils/eventColors';

interface DropdownPosition {
  top: number;
  left: number;
  width: number;
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
  prefix?: string;
  dropdownWidth?: string;
  alignRight?: boolean;
  dropdownTitle?: string;
  footerNote?: string;
  footerIcon?: React.ComponentType<IconComponentProps>;
  cleanStyle?: boolean;
}

export const EnhancedDropdown: React.FC<EnhancedDropdownProps> = ({
  options,
  value,
  onChange,
  placeholder = 'Select option',
  className = '',
  disabled = false,
  compactMode = false,
  customTriggerLabel,
  prefix,
  dropdownWidth,
  alignRight = false,
  dropdownTitle,
  footerNote,
  footerIcon: FooterIcon,
  cleanStyle = false
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const [dropdownStyle, setDropdownStyle] = useState<{ animation: string }>({ animation: '' });
  const [dropdownPosition, setDropdownPosition] = useState<DropdownPosition | null>(null);
  const [expandedSubmenu, setExpandedSubmenu] = useState<string | null>(null);
  const [submenuPosition, setSubmenuPosition] = useState<{ top: number; left: number; openLeft: boolean } | null>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const submenuRef = useRef<HTMLDivElement>(null);

  const selectedOption = options.find((opt) => opt.value === value) ||
    (value.includes(':') ? options.find((opt) => opt.submenu && value.startsWith(opt.value + ':')) : undefined);

  useEffect(() => {
    if (!isOpen) setExpandedSubmenu(null);
  }, [isOpen]);

  // Calculate position before paint - for portal rendering
  useLayoutEffect(() => {
    if (!isOpen || !buttonRef.current) return;

    const calculatePosition = () => {
      if (!buttonRef.current) return null;

      const rect = buttonRef.current.getBoundingClientRect();
      const dropdownHeight = 300; // Approximate max height
      const dropdownWidthPx = dropdownWidth ? parseInt(dropdownWidth) || rect.width : rect.width;
      const spaceBelow = window.innerHeight - rect.bottom;
      const spaceAbove = rect.top;
      const shouldOpenUpward = spaceBelow < dropdownHeight && spaceAbove > dropdownHeight;

      // Calculate horizontal position
      let left: number;
      if (alignRight) {
        // Align right edge of dropdown with right edge of button
        left = rect.right - dropdownWidthPx;
        // Ensure doesn't go off left side
        if (left < 8) {
          left = 8;
        }
      } else {
        // Align left edge of dropdown with left edge of button
        left = rect.left;
        // Ensure doesn't go off right side
        if (left + dropdownWidthPx > window.innerWidth - 8) {
          left = window.innerWidth - dropdownWidthPx - 8;
        }
      }

      // Calculate vertical position
      const top = shouldOpenUpward
        ? rect.top - dropdownHeight - 8
        : rect.bottom + 4;

      return { top, left, width: rect.width, shouldOpenUpward };
    };

    const pos = calculatePosition();
    if (pos) {
      setDropdownPosition({ top: pos.top, left: pos.left, width: pos.width });
      setDropdownStyle({
        animation: `${pos.shouldOpenUpward ? 'dropdownSlideUp' : 'dropdownSlideDown'} 0.15s cubic-bezier(0.16, 1, 0.3, 1)`
      });
    }
  }, [isOpen, alignRight, dropdownWidth]);

  // Event listeners
  useEffect(() => {
    if (!isOpen) return;

    const handleClickOutside = (e: MouseEvent) => {
      const target = e.target as Node;
      if (!dropdownRef.current?.contains(target) && !buttonRef.current?.contains(target) && !submenuRef.current?.contains(target)) {
        setIsOpen(false);
      }
    };

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };

    // Close on scroll to prevent dropdown from being mispositioned
    const handleScroll = () => {
      setIsOpen(false);
    };

    document.addEventListener('mousedown', handleClickOutside);
    document.addEventListener('keydown', handleEscape);
    window.addEventListener('scroll', handleScroll, true);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
      document.removeEventListener('keydown', handleEscape);
      window.removeEventListener('scroll', handleScroll, true);
    };
  }, [isOpen]);

  const handleSelect = useCallback((optionValue: string) => {
    onChange(optionValue);
    setIsOpen(false);
  }, [onChange]);

  const handleSubmenuToggle = useCallback((optionValue: string, triggerElement: HTMLButtonElement) => {
    if (expandedSubmenu === optionValue) {
      setExpandedSubmenu(null);
      setSubmenuPosition(null);
    } else {
      const rect = triggerElement.getBoundingClientRect();
      const submenuWidth = 256;
      const viewportWidth = window.innerWidth;
      const spaceOnRight = viewportWidth - rect.right;
      const openLeft = spaceOnRight < submenuWidth && rect.left > submenuWidth;

      setSubmenuPosition({
        top: rect.top,
        left: openLeft ? rect.left - submenuWidth - 4 : rect.right + 4,
        openLeft
      });
      setExpandedSubmenu(optionValue);
    }
  }, [expandedSubmenu]);

  const displayLabel = customTriggerLabel
    ? customTriggerLabel
    : selectedOption
    ? (prefix ? `${prefix} ` : '') + (compactMode && selectedOption.shortLabel ? selectedOption.shortLabel : selectedOption.label)
    : placeholder;

  return (
    <div className={`relative ${className}`}>
      <button
        ref={buttonRef}
        type="button"
        onClick={() => !disabled && setIsOpen(!isOpen)}
        disabled={disabled}
        className={`ed-trigger w-full px-3 py-2 rounded-lg border text-left flex items-center justify-between text-sm themed-card text-themed-primary ${
          isOpen ? 'border-themed-focus' : 'border-themed-primary'
        } ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}`}
      >
        <div className="flex items-center gap-1.5 flex-1 truncate">
          {selectedOption?.icon && (
            <selectedOption.icon className="flex-shrink-0 text-[var(--theme-primary)]" size={16} />
          )}
          <span className={compactMode ? 'font-medium' : 'truncate'}>{displayLabel}</span>
        </div>
        <ChevronDown
          size={16}
          className={`flex-shrink-0 transition-transform duration-200 text-themed-primary ${isOpen ? 'rotate-180' : ''}`}
        />
      </button>

      {/* Dropdown - rendered via portal to escape stacking context */}
      {isOpen && dropdownPosition && createPortal(
        <div
          ref={dropdownRef}
          className="ed-dropdown fixed rounded-lg border border-themed-primary overflow-hidden bg-themed-secondary max-w-[calc(100vw-32px)] shadow-[0_10px_25px_-5px_rgba(0,0,0,0.3),0_8px_10px_-6px_rgba(0,0,0,0.2)] z-[50000]"
          style={{
            top: dropdownPosition.top,
            left: dropdownPosition.left,
            width: dropdownWidth || dropdownPosition.width,
            minWidth: dropdownPosition.width,
            animation: dropdownStyle.animation
          }}
        >
          {dropdownTitle && (
            <div className="px-3 py-2 text-sm font-medium border-b border-themed-primary bg-themed-secondary text-themed-secondary">
              {dropdownTitle}
            </div>
          )}

          <CustomScrollbar maxHeight={cleanStyle ? 'none' : '280px'} paddingMode="compact">
            <div className="py-1">
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
                    <button
                      type="button"
                      onClick={(e) => handleSubmenuToggle(option.value, e.currentTarget)}
                      className={`ed-option w-full px-3 py-2.5 text-left text-sm cursor-pointer ${value.startsWith(option.value + ':') || expandedSubmenu === option.value ? 'ed-option-selected' : ''}`}
                      title={option.description || option.label}
                    >
                      <div className="flex items-start gap-3">
                        {!cleanStyle && option.icon && (
                          <option.icon
                            className={`flex-shrink-0 mt-0.5 ${
                              value.startsWith(option.value + ':') ? 'text-[var(--theme-primary)]' : 'text-themed-secondary'
                            }`}
                            size={16}
                          />
                        )}
                        <div className="flex flex-col flex-1 min-w-0">
                          <span className={`font-medium truncate ${value.startsWith(option.value + ':') ? 'text-[var(--theme-primary)]' : 'text-themed-primary'}`}>
                            {option.label}
                          </span>
                          {option.description && (
                            <span className="text-xs mt-0.5 leading-relaxed text-themed-secondary">
                              {option.description}
                            </span>
                          )}
                        </div>
                        {option.rightLabel && (
                          <span className={`flex-shrink-0 text-xs font-medium mr-1 ${
                            value.startsWith(option.value + ':') ? 'text-[var(--theme-primary)]' : 'text-themed-secondary'
                          }`}>
                            {option.rightLabel}
                          </span>
                        )}
                        <ChevronRight
                          size={16}
                          className={`flex-shrink-0 mt-0.5 transition-transform duration-200 text-themed-muted ${expandedSubmenu === option.value ? (submenuPosition?.openLeft ? '-rotate-90' : 'rotate-90') : ''}`}
                        />
                      </div>
                    </button>

                    {expandedSubmenu === option.value && submenuPosition && createPortal(
                      <div
                        ref={submenuRef}
                        className="ed-dropdown fixed w-64 rounded-lg border border-themed-primary overflow-hidden z-[10001] bg-themed-secondary shadow-[0_10px_25px_-5px_rgba(0,0,0,0.3),0_8px_10px_-6px_rgba(0,0,0,0.2)] animate-[dropdownSlideDown_0.15s_cubic-bezier(0.16,1,0.3,1)]"
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
                        <CustomScrollbar maxHeight="240px" paddingMode="none">
                          <div className="py-1">
                            {option.submenu.map((subItem) => {
                              const isSubSelected = value === `${option.value}:${subItem.value}`;
                              return (
                                <button
                                  key={subItem.value}
                                  type="button"
                                  onClick={() => handleSelect(`${option.value}:${subItem.value}`)}
                                  className={`ed-submenu-option w-full flex items-center gap-2.5 px-3 py-2.5 text-sm ${
                                    isSubSelected
                                      ? 'ed-submenu-selected bg-[var(--theme-primary)] text-themed-button'
                                      : 'bg-transparent text-themed-primary'
                                  }`}
                                >
                                  {(subItem.colorIndex || subItem.color) && (
                                    <div
                                      className="w-2.5 h-2.5 rounded-full flex-shrink-0"
                                      style={{ backgroundColor: subItem.colorIndex ? getEventColorVar(subItem.colorIndex) : subItem.color }}
                                    />
                                  )}
                                  <div className="flex-1 min-w-0 text-left">
                                    <div className="flex items-center gap-1.5">
                                      <span className="font-medium truncate">{subItem.label}</span>
                                      {subItem.badge && (
                                        <span
                                          className="px-1.5 py-0.5 text-[10px] rounded-full font-medium"
                                          style={{
                                            backgroundColor: isSubSelected ? 'rgba(255,255,255,0.2)' : `color-mix(in srgb, ${subItem.badgeColor || 'var(--theme-status-success)'} 20%, transparent)`,
                                            color: isSubSelected ? 'var(--theme-button-text)' : (subItem.badgeColor || 'var(--theme-status-success)')
                                          }}
                                        >
                                          {subItem.badge}
                                        </span>
                                      )}
                                    </div>
                                    {subItem.description && (
                                      <div className={`text-xs truncate ${isSubSelected ? 'text-white/70' : 'text-themed-muted'}`}>
                                        {subItem.description}
                                      </div>
                                    )}
                                  </div>
                                  {isSubSelected && <Check size={14} className="flex-shrink-0 text-themed-button" />}
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
                          className={`ed-option w-full px-3 py-2.5 text-left text-sm ${option.disabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'} ${isSelected ? 'ed-option-selected' : ''}`}
                        >
                          <div className="flex items-start gap-3">
                            {!cleanStyle && option.icon && (
                              <option.icon
                                className={`flex-shrink-0 mt-0.5 ${isSelected ? 'text-[var(--theme-primary)]' : 'text-themed-secondary'}`}
                                size={16}
                              />
                            )}
                            <div className="flex flex-col flex-1 min-w-0">
                              <span className={`font-medium truncate ${isSelected ? 'text-[var(--theme-primary)]' : 'text-themed-primary'}`}>
                                {option.label}
                              </span>
                              {option.description && (
                                <span className="text-xs mt-0.5 leading-relaxed text-themed-secondary">
                                  {option.description}
                                </span>
                              )}
                            </div>
                            {option.rightLabel && (
                              <span className={`flex-shrink-0 text-xs font-medium ${isSelected ? 'text-[var(--theme-primary)]' : 'text-themed-secondary'}`}>
                                {option.rightLabel}
                              </span>
                            )}
                            {!cleanStyle && isSelected && (
                              <Check size={16} className="flex-shrink-0 mt-0.5 text-[var(--theme-primary)]" />
                            )}
                          </div>
                        </button>
                      );
                      return option.tooltip ? <Tooltip content={option.tooltip} className="w-full">{buttonContent}</Tooltip> : buttonContent;
                    })()}
                  </React.Fragment>
                )
              )}
            </div>
          </CustomScrollbar>

          {footerNote && (
            <div className="px-3 py-2.5 text-xs border-t border-themed-primary flex items-start gap-2 text-themed-secondary bg-themed-tertiary">
              {FooterIcon && <FooterIcon className="flex-shrink-0 mt-0.5 text-themed-warning" size={14} />}
              <span className="leading-relaxed">{footerNote}</span>
            </div>
          )}
        </div>,
        document.body
      )}
    </div>
  );
};
