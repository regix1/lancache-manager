import React, { useState, useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, ChevronRight, Check } from 'lucide-react';
import { CustomScrollbar } from './CustomScrollbar';

/** Props interface for icon components used in dropdowns */
interface IconComponentProps {
  size?: number;
  className?: string;
  style?: React.CSSProperties;
}

export interface SubmenuOption {
  value: string;
  label: string;
  description?: string;
  color?: string; // Optional color indicator
  badge?: string; // Optional badge text (e.g., "Live")
  badgeColor?: string; // Badge color
}

export interface DropdownOption {
  value: string;
  label: string;
  shortLabel?: string; // Compact label for button display
  description?: string;
  icon?: React.ComponentType<IconComponentProps>;
  disabled?: boolean;
  rightLabel?: string; // Right-aligned badge/label (e.g., "10s", "1m")
  submenu?: SubmenuOption[]; // Optional submenu items
  submenuTitle?: string; // Optional title for submenu
}

interface EnhancedDropdownProps {
  options: DropdownOption[];
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  className?: string;
  disabled?: boolean;
  compactMode?: boolean; // When true, shows shortLabel in button trigger
  customTriggerLabel?: string; // Optional custom label to override button display
  prefix?: string; // Optional prefix shown before the selected value in button (e.g., "Sort:")
  dropdownWidth?: string; // Custom width for dropdown menu (e.g., 'w-64', '16rem')
  alignRight?: boolean; // When true, dropdown aligns to the right of the button
  dropdownTitle?: string; // Optional title/subtitle at the top of the dropdown
  footerNote?: string; // Optional footer note/warning at the bottom
  footerIcon?: React.ComponentType<IconComponentProps>; // Optional icon for footer note
  cleanStyle?: boolean; // When true, uses the clean style without icons in options
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
  const [openUpward, setOpenUpward] = useState(false);
  const [horizontalPosition, setHorizontalPosition] = useState<'left' | 'right' | 'center'>('left');
  const [horizontalOffset, setHorizontalOffset] = useState(0);
  const [expandedSubmenu, setExpandedSubmenu] = useState<string | null>(null);
  const [submenuPosition, setSubmenuPosition] = useState<{ top: number; left: number; openLeft: boolean } | null>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const submenuRef = useRef<HTMLDivElement>(null);

  // Find selected option - also check for parent option when submenu item is selected
  const selectedOption = options.find((opt) => opt.value === value) ||
    (value.includes(':') ? options.find((opt) => opt.submenu && value.startsWith(opt.value + ':')) : undefined);

  // Reset expanded submenu when dropdown closes
  useEffect(() => {
    if (!isOpen) {
      setExpandedSubmenu(null);
    }
  }, [isOpen]);

  // Check if dropdown should open upward and handle horizontal positioning
  useEffect(() => {
    if (isOpen && buttonRef.current && dropdownRef.current) {
      const buttonRect = buttonRef.current.getBoundingClientRect();
      const dropdownHeight = 240; // maxHeight from styles
      const spaceBelow = window.innerHeight - buttonRect.bottom;
      const spaceAbove = buttonRect.top;

      // Open upward if not enough space below but enough space above
      if (spaceBelow < dropdownHeight && spaceAbove > dropdownHeight) {
        setOpenUpward(true);
      } else {
        setOpenUpward(false);
      }

      // Calculate horizontal positioning
      const dropdownElement = dropdownRef.current;
      const dropdownWidth = dropdownElement.offsetWidth;
      const viewportWidth = window.innerWidth;
      const buttonLeft = buttonRect.left;
      const buttonRight = buttonRect.right;

      // Calculate space on both sides
      const spaceOnLeft = buttonRight;

      // Determine optimal horizontal position
      if (alignRight) {
        // If alignRight is explicitly set, check if it fits
        const wouldOverflowLeft = buttonRight - dropdownWidth < 0;
        if (wouldOverflowLeft) {
          // Not enough space on right alignment, switch to left
          setHorizontalPosition('left');
          // Check if it still overflows on the right
          if (buttonLeft + dropdownWidth > viewportWidth) {
            // Dropdown is wider than available space, offset it to fit
            const offset = viewportWidth - (buttonLeft + dropdownWidth) - 16; // 16px padding from edge
            setHorizontalOffset(offset);
          } else {
            setHorizontalOffset(0);
          }
        } else {
          setHorizontalPosition('right');
          setHorizontalOffset(0);
        }
      } else {
        // Default left alignment, check if it fits
        const wouldOverflowRight = buttonLeft + dropdownWidth > viewportWidth;
        if (wouldOverflowRight) {
          // Not enough space with left alignment
          // Check if right alignment would be better
          if (spaceOnLeft >= dropdownWidth) {
            setHorizontalPosition('right');
            setHorizontalOffset(0);
          } else {
            // Neither side fits perfectly, offset to keep within viewport
            const offset = viewportWidth - (buttonLeft + dropdownWidth) - 16; // 16px padding from edge
            setHorizontalPosition('left');
            setHorizontalOffset(offset);
          }
        } else {
          setHorizontalPosition('left');
          setHorizontalOffset(0);
        }
      }
    }
  }, [isOpen, alignRight]);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;
      const isInsideDropdown = dropdownRef.current?.contains(target);
      const isInsideButton = buttonRef.current?.contains(target);
      const isInsideSubmenu = submenuRef.current?.contains(target);

      if (!isInsideDropdown && !isInsideButton && !isInsideSubmenu) {
        setIsOpen(false);
      }
    };

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setIsOpen(false);
      }
    };

    const handleResize = () => {
      // Force repositioning on resize (e.g., mobile orientation change)
      if (isOpen && buttonRef.current && dropdownRef.current) {
        const buttonRect = buttonRef.current.getBoundingClientRect();
        const dropdownElement = dropdownRef.current;
        const dropdownWidth = dropdownElement.offsetWidth;
        const viewportWidth = window.innerWidth;
        const buttonLeft = buttonRect.left;
        const buttonRight = buttonRect.right;

        const spaceOnLeft = buttonRight;
        const wouldOverflowRight = buttonLeft + dropdownWidth > viewportWidth;

        if (alignRight) {
          const wouldOverflowLeft = buttonRight - dropdownWidth < 0;
          if (wouldOverflowLeft) {
            setHorizontalPosition('left');
            if (buttonLeft + dropdownWidth > viewportWidth) {
              const offset = viewportWidth - (buttonLeft + dropdownWidth) - 16;
              setHorizontalOffset(offset);
            } else {
              setHorizontalOffset(0);
            }
          } else {
            setHorizontalPosition('right');
            setHorizontalOffset(0);
          }
        } else {
          if (wouldOverflowRight) {
            if (spaceOnLeft >= dropdownWidth) {
              setHorizontalPosition('right');
              setHorizontalOffset(0);
            } else {
              const offset = viewportWidth - (buttonLeft + dropdownWidth) - 16;
              setHorizontalPosition('left');
              setHorizontalOffset(offset);
            }
          } else {
            setHorizontalPosition('left');
            setHorizontalOffset(0);
          }
        }
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      document.addEventListener('keydown', handleEscape);
      window.addEventListener('resize', handleResize);
      window.addEventListener('scroll', handleResize, true); // Capture scroll events
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
        document.removeEventListener('keydown', handleEscape);
        window.removeEventListener('resize', handleResize);
        window.removeEventListener('scroll', handleResize, true);
      };
    }
  }, [isOpen, alignRight]);

  const handleSelect = (optionValue: string) => {
    onChange(optionValue);
    setIsOpen(false);
  };

  const handleSubmenuToggle = (optionValue: string, triggerElement: HTMLButtonElement) => {
    if (expandedSubmenu === optionValue) {
      setExpandedSubmenu(null);
      setSubmenuPosition(null);
    } else {
      // Calculate position based on trigger element
      const rect = triggerElement.getBoundingClientRect();
      const submenuWidth = 256; // w-64 = 16rem = 256px
      const viewportWidth = window.innerWidth;

      // Determine if submenu should open to the left or right
      const spaceOnRight = viewportWidth - rect.right;
      const spaceOnLeft = rect.left;
      const openLeft = spaceOnRight < submenuWidth && spaceOnLeft > submenuWidth;

      setSubmenuPosition({
        top: rect.top,
        left: openLeft ? rect.left - submenuWidth - 4 : rect.right + 4,
        openLeft
      });
      setExpandedSubmenu(optionValue);
    }
  };

  return (
    <>
      <style>{`
        @keyframes dropdownSlide {
          from {
            opacity: 0;
            transform: translateY(-8px) scale(0.98);
          }
          to {
            opacity: 1;
            transform: translateY(0) scale(1);
          }
        }
        @keyframes dropdownSlideUp {
          from {
            opacity: 0;
            transform: translateY(8px) scale(0.98);
          }
          to {
            opacity: 1;
            transform: translateY(0) scale(1);
          }
        }
        @keyframes submenuExpand {
          from {
            opacity: 0;
            max-height: 0;
          }
          to {
            opacity: 1;
            max-height: 300px;
          }
        }
      `}</style>
      <div className={`relative ${className}`}>
        <button
        ref={buttonRef}
        type="button"
        onClick={() => !disabled && setIsOpen(!isOpen)}
        disabled={disabled}
        className={`${compactMode ? 'w-auto' : 'w-full'} px-3 py-2 rounded-lg border text-[var(--theme-text-primary)] text-left transition-all flex items-center justify-between text-sm focus:outline-none focus:ring-0 focus:shadow-none active:outline-none active:ring-0 ${disabled ? 'opacity-50 cursor-not-allowed' : ''}`}
        style={{
          backgroundColor: 'var(--theme-bg-secondary)',
          borderColor: isOpen ? 'var(--theme-border-focus)' : 'var(--theme-border-primary)',
          outline: 'none !important',
          boxShadow: 'none !important',
          WebkitTapHighlightColor: 'transparent'
        }}
        onMouseEnter={(e) =>
          !disabled && (e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)')
        }
        onMouseLeave={(e) =>
          !disabled && (e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)')
        }
        onFocus={(e) => {
          if (!disabled) {
            e.currentTarget.style.borderColor = 'var(--theme-border-focus)';
          }
        }}
        onBlur={(e) => {
          if (!disabled && !isOpen) {
            e.currentTarget.style.borderColor = 'var(--theme-border-primary)';
          }
        }}
      >
        <div className="flex items-center gap-1.5 flex-1 truncate">
          {selectedOption?.icon && (
            <selectedOption.icon
              className="flex-shrink-0"
              size={16}
              style={{ color: 'var(--theme-primary)' }}
            />
          )}
          <span className={`${compactMode ? 'font-medium' : 'truncate'}`}>
            {customTriggerLabel
              ? customTriggerLabel
              : selectedOption
              ? (prefix ? `${prefix} ` : '') + (compactMode && selectedOption.shortLabel
                  ? selectedOption.shortLabel
                  : selectedOption.label)
              : placeholder}
          </span>
        </div>
        <ChevronDown
          size={16}
          className={`flex-shrink-0 transition-transform duration-200 ${isOpen ? 'rotate-180' : ''}`}
          style={{ color: 'var(--theme-text-primary)' }}
        />
      </button>

      {isOpen && (
        <div
          ref={dropdownRef}
          className={`absolute ${dropdownWidth || 'w-full'} ${horizontalPosition === 'right' ? 'right-0' : 'left-0'} rounded-lg border z-[9999] overflow-hidden ${
            openUpward ? 'bottom-full mb-2' : 'mt-2'
          }`}
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            borderColor: 'var(--theme-border-primary)',
            maxWidth: 'calc(100vw - 32px)', // Account for 16px padding on each side
            transform: horizontalOffset !== 0 ? `translateX(${horizontalOffset}px)` : 'none',
            boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.3), 0 8px 10px -6px rgba(0, 0, 0, 0.2), 0 0 0 1px rgba(0, 0, 0, 0.1)',
            animation: openUpward ? 'dropdownSlideUp 0.15s cubic-bezier(0.16, 1, 0.3, 1)' : 'dropdownSlide 0.15s cubic-bezier(0.16, 1, 0.3, 1)'
          }}
        >
          {dropdownTitle && (
            <div
              className="px-3 py-2 text-sm font-medium border-b"
              style={{
                color: 'var(--theme-text-secondary)',
                borderColor: 'var(--theme-border-primary)',
                backgroundColor: 'var(--theme-bg-secondary)'
              }}
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
                    style={{
                      color: 'var(--theme-text-muted)',
                      borderColor: 'var(--theme-border-primary)',
                      backgroundColor: 'var(--theme-bg-tertiary)'
                    }}
                  >
                    {option.label}
                  </div>
                ) : option.submenu && option.submenu.length > 0 ? (
                  // Option with submenu - click to expand as side panel
                  <React.Fragment key={option.value}>
                    <button
                      type="button"
                      onClick={(e) => handleSubmenuToggle(option.value, e.currentTarget)}
                      className={`w-full px-3 py-2.5 text-left text-sm transition-all duration-150 hover:bg-[var(--theme-bg-tertiary)] cursor-pointer ${
                        value.startsWith(option.value + ':') || expandedSubmenu === option.value
                          ? 'bg-[var(--theme-bg-tertiary)]'
                          : ''
                      }`}
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
                          <span
                            className="flex-shrink-0 text-xs font-medium mr-1"
                            style={{ color: value.startsWith(option.value + ':') ? 'var(--theme-primary)' : 'var(--theme-text-secondary)' }}
                          >
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

                    {/* Submenu rendered via portal to escape overflow clipping */}
                    {expandedSubmenu === option.value && submenuPosition && createPortal(
                      <div
                        ref={submenuRef}
                        className="fixed w-64 rounded-lg border overflow-hidden z-[10001]"
                        style={{
                          top: submenuPosition.top,
                          left: submenuPosition.left,
                          backgroundColor: 'var(--theme-bg-secondary)',
                          borderColor: 'var(--theme-border-primary)',
                          boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.3), 0 8px 10px -6px rgba(0, 0, 0, 0.2)',
                          animation: 'dropdownSlide 0.15s cubic-bezier(0.16, 1, 0.3, 1)'
                        }}
                      >
                        {option.submenuTitle && (
                          <div
                            className="px-3 py-2 text-xs font-semibold border-b"
                            style={{
                              color: 'var(--theme-text-secondary)',
                              borderColor: 'var(--theme-border-primary)',
                              backgroundColor: 'var(--theme-bg-tertiary)'
                            }}
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
                                  className="w-full flex items-center gap-2.5 px-3 py-2.5 text-sm transition-colors"
                                  style={{
                                    backgroundColor: isSubSelected ? 'var(--theme-primary)' : 'transparent',
                                    color: isSubSelected ? 'var(--theme-button-text)' : 'var(--theme-text-primary)'
                                  }}
                                  onMouseEnter={(e) => {
                                    if (!isSubSelected) e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)';
                                  }}
                                  onMouseLeave={(e) => {
                                    if (!isSubSelected) e.currentTarget.style.backgroundColor = 'transparent';
                                  }}
                                >
                                  {subItem.color && (
                                    <div
                                      className="w-2.5 h-2.5 rounded-full flex-shrink-0"
                                      style={{ backgroundColor: subItem.color }}
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
                                      <div
                                        className="text-xs truncate"
                                        style={{ color: isSubSelected ? 'rgba(255,255,255,0.7)' : 'var(--theme-text-muted)' }}
                                      >
                                        {subItem.description}
                                      </div>
                                    )}
                                  </div>
                                  {isSubSelected && (
                                    <Check
                                      size={14}
                                      className="flex-shrink-0"
                                      style={{ color: 'var(--theme-button-text)' }}
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
                  <button
                    key={option.value}
                    type="button"
                    onClick={() => !option.disabled && handleSelect(option.value)}
                    disabled={option.disabled}
                    className={`w-full px-3 py-2.5 text-left text-sm transition-all duration-150 ${
                      option.disabled
                        ? 'opacity-40 cursor-not-allowed'
                        : 'hover:bg-[var(--theme-bg-tertiary)] cursor-pointer'
                    } ${
                      option.value === value
                        ? 'bg-[var(--theme-bg-tertiary)]'
                        : ''
                    } ${
                      options.findIndex((opt) => opt.value === 'divider') !== -1 &&
                      options.findIndex((opt) => opt.value === option.value) >
                        options.findIndex((opt) => opt.value === 'divider')
                        ? 'opacity-75 text-xs pl-6'
                        : ''
                    }`}
                    title={option.description || option.label}
                  >
                    <div className="flex items-start gap-3">
                      {!cleanStyle && option.icon && (
                        <option.icon
                          className="flex-shrink-0 mt-0.5"
                          size={16}
                          style={{ color: option.value === value ? 'var(--theme-primary)' : 'var(--theme-text-secondary)' }}
                        />
                      )}
                      <div className="flex flex-col flex-1 min-w-0">
                        <span className={`font-medium truncate ${option.value === value ? 'text-[var(--theme-primary)]' : 'text-[var(--theme-text-primary)]'}`}>
                          {option.label}
                        </span>
                        {option.description && (
                          <span className="text-xs mt-0.5 leading-relaxed" style={{ color: 'var(--theme-text-secondary)' }}>
                            {option.description}
                          </span>
                        )}
                      </div>
                      {option.rightLabel && (
                        <span
                          className="flex-shrink-0 text-xs font-medium"
                          style={{ color: option.value === value ? 'var(--theme-primary)' : 'var(--theme-text-secondary)' }}
                        >
                          {option.rightLabel}
                        </span>
                      )}
                      {!cleanStyle && option.value === value && (
                        <Check
                          size={16}
                          className="flex-shrink-0 mt-0.5"
                          style={{ color: 'var(--theme-primary)' }}
                        />
                      )}
                    </div>
                  </button>
                )
              )}
            </div>
          </CustomScrollbar>
          {footerNote && (
            <div
              className="px-3 py-2.5 text-xs border-t flex items-start gap-2"
              style={{
                color: 'var(--theme-text-secondary)',
                borderColor: 'var(--theme-border-primary)',
                backgroundColor: 'var(--theme-bg-tertiary)'
              }}
            >
              {FooterIcon && (
                <FooterIcon
                  className="flex-shrink-0 mt-0.5"
                  size={14}
                  style={{ color: 'var(--theme-warning)' }}
                />
              )}
              <span className="leading-relaxed">{footerNote}</span>
            </div>
          )}
        </div>
      )}
      </div>
    </>
  );
};
