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
        className={`ed-trigger w-full px-3 py-2 rounded-lg border text-left flex items-center justify-between text-sm ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'}`}
        style={{
          backgroundColor: 'var(--theme-card-bg)',
          borderColor: isOpen ? 'var(--theme-border-focus)' : 'var(--theme-border-primary)',
          color: 'var(--theme-text-primary)'
        }}
      >
        <div className="flex items-center gap-1.5 flex-1 truncate">
          {selectedOption?.icon && (
            <selectedOption.icon className="flex-shrink-0" size={16} style={{ color: 'var(--theme-primary)' }} />
          )}
          <span className={compactMode ? 'font-medium' : 'truncate'}>{displayLabel}</span>
        </div>
        <ChevronDown
          size={16}
          className={`flex-shrink-0 transition-transform duration-200 ${isOpen ? 'rotate-180' : ''}`}
          style={{ color: 'var(--theme-text-primary)' }}
        />
      </button>

      {/* Dropdown - rendered via portal to escape stacking context */}
      {isOpen && dropdownPosition && createPortal(
        <div
          ref={dropdownRef}
          className="ed-dropdown fixed rounded-lg border overflow-hidden"
          style={{
            top: dropdownPosition.top,
            left: dropdownPosition.left,
            width: dropdownWidth || dropdownPosition.width,
            minWidth: dropdownPosition.width,
            backgroundColor: 'var(--theme-bg-secondary)',
            borderColor: 'var(--theme-border-primary)',
            maxWidth: 'calc(100vw - 32px)',
            boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.3), 0 8px 10px -6px rgba(0, 0, 0, 0.2)',
            animation: dropdownStyle.animation,
            zIndex: 50000
          }}
        >
          {dropdownTitle && (
            <div
              className="px-3 py-2 text-sm font-medium border-b"
              style={{ color: 'var(--theme-text-secondary)', borderColor: 'var(--theme-border-primary)', backgroundColor: 'var(--theme-bg-secondary)' }}
            >
              {dropdownTitle}
            </div>
          )}

          <CustomScrollbar maxHeight={cleanStyle ? 'none' : '280px'} paddingMode="compact">
            <div className="py-1">
              {options.map((option) =>
                option.value === 'divider' ? (
                  <div
                    key={option.value}
                    className="px-3 py-2 text-xs font-medium border-t mt-1 mb-1 truncate"
                    style={{ color: 'var(--theme-text-muted)', borderColor: 'var(--theme-border-primary)', backgroundColor: 'var(--theme-bg-tertiary)' }}
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
                            className="flex-shrink-0 mt-0.5"
                            size={16}
                            style={{ color: value.startsWith(option.value + ':') ? 'var(--theme-primary)' : 'var(--theme-text-secondary)' }}
                          />
                        )}
                        <div className="flex flex-col flex-1 min-w-0">
                          <span className={`font-medium truncate ${value.startsWith(option.value + ':') ? 'text-[var(--theme-primary)]' : 'text-[var(--theme-text-primary)]'}`}>
                            {option.label}
                          </span>
                          {option.description && (
                            <span className="text-xs mt-0.5 leading-relaxed" style={{ color: 'var(--theme-text-secondary)' }}>
                              {option.description}
                            </span>
                          )}
                        </div>
                        {option.rightLabel && (
                          <span className="flex-shrink-0 text-xs font-medium mr-1" style={{ color: value.startsWith(option.value + ':') ? 'var(--theme-primary)' : 'var(--theme-text-secondary)' }}>
                            {option.rightLabel}
                          </span>
                        )}
                        <ChevronRight
                          size={16}
                          className={`flex-shrink-0 mt-0.5 transition-transform duration-200 ${expandedSubmenu === option.value ? (submenuPosition?.openLeft ? '-rotate-90' : 'rotate-90') : ''}`}
                          style={{ color: 'var(--theme-text-muted)' }}
                        />
                      </div>
                    </button>

                    {expandedSubmenu === option.value && submenuPosition && createPortal(
                      <div
                        ref={submenuRef}
                        className="ed-dropdown fixed w-64 rounded-lg border overflow-hidden z-[10001]"
                        style={{
                          top: submenuPosition.top,
                          left: submenuPosition.left,
                          backgroundColor: 'var(--theme-bg-secondary)',
                          borderColor: 'var(--theme-border-primary)',
                          boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.3), 0 8px 10px -6px rgba(0, 0, 0, 0.2)',
                          animation: 'dropdownSlideDown 0.15s cubic-bezier(0.16, 1, 0.3, 1)'
                        }}
                      >
                        {option.submenuTitle && (
                          <div
                            className="px-3 py-2 text-xs font-semibold border-b"
                            style={{ color: 'var(--theme-text-secondary)', borderColor: 'var(--theme-border-primary)', backgroundColor: 'var(--theme-bg-tertiary)' }}
                          >
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
                                  className={`ed-submenu-option w-full flex items-center gap-2.5 px-3 py-2.5 text-sm ${isSubSelected ? 'ed-submenu-selected' : ''}`}
                                  style={{
                                    backgroundColor: isSubSelected ? 'var(--theme-primary)' : 'transparent',
                                    color: isSubSelected ? 'var(--theme-button-text)' : 'var(--theme-text-primary)'
                                  }}
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
                                      <div className="text-xs truncate" style={{ color: isSubSelected ? 'rgba(255,255,255,0.7)' : 'var(--theme-text-muted)' }}>
                                        {subItem.description}
                                      </div>
                                    )}
                                  </div>
                                  {isSubSelected && <Check size={14} className="flex-shrink-0" style={{ color: 'var(--theme-button-text)' }} />}
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
                                className="flex-shrink-0 mt-0.5"
                                size={16}
                                style={{ color: isSelected ? 'var(--theme-primary)' : 'var(--theme-text-secondary)' }}
                              />
                            )}
                            <div className="flex flex-col flex-1 min-w-0">
                              <span className={`font-medium truncate ${isSelected ? 'text-[var(--theme-primary)]' : 'text-[var(--theme-text-primary)]'}`}>
                                {option.label}
                              </span>
                              {option.description && (
                                <span className="text-xs mt-0.5 leading-relaxed" style={{ color: 'var(--theme-text-secondary)' }}>
                                  {option.description}
                                </span>
                              )}
                            </div>
                            {option.rightLabel && (
                              <span className="flex-shrink-0 text-xs font-medium" style={{ color: isSelected ? 'var(--theme-primary)' : 'var(--theme-text-secondary)' }}>
                                {option.rightLabel}
                              </span>
                            )}
                            {!cleanStyle && isSelected && (
                              <Check size={16} className="flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-primary)' }} />
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
            <div
              className="px-3 py-2.5 text-xs border-t flex items-start gap-2"
              style={{ color: 'var(--theme-text-secondary)', borderColor: 'var(--theme-border-primary)', backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              {FooterIcon && <FooterIcon className="flex-shrink-0 mt-0.5" size={14} style={{ color: 'var(--theme-warning)' }} />}
              <span className="leading-relaxed">{footerNote}</span>
            </div>
          )}
        </div>,
        document.body
      )}
    </div>
  );
};
