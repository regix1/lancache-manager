import React, { useState, useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, Check } from 'lucide-react';
import { CustomScrollbar } from './CustomScrollbar';

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
}

export const MultiSelectDropdown: React.FC<MultiSelectDropdownProps> = ({
  options,
  values,
  onChange,
  placeholder = 'Select options',
  className = '',
  disabled = false,
  dropdownWidth,
  alignRight = false,
  title,
  minSelections = 1,
  maxSelections
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const [openUpward, setOpenUpward] = useState(false);
  const [horizontalPosition, setHorizontalPosition] = useState<'left' | 'right' | 'center'>('left');
  const [horizontalOffset, setHorizontalOffset] = useState(0);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  const selectedOptions = options.filter((opt) => values.includes(opt.value));
  const selectedCount = selectedOptions.length;

  // Generate display label
  const getDisplayLabel = () => {
    if (selectedCount === 0) return placeholder;
    if (selectedCount === 1) return selectedOptions[0].label;
    if (selectedCount === options.length) return 'All selected';
    return `${selectedCount} selected`;
  };

  // Check if dropdown should open upward and handle horizontal positioning
  useEffect(() => {
    if (isOpen && buttonRef.current && dropdownRef.current) {
      const buttonRect = buttonRef.current.getBoundingClientRect();
      const dropdownHeight = 240;
      const spaceBelow = window.innerHeight - buttonRect.bottom;
      const spaceAbove = buttonRect.top;

      if (spaceBelow < dropdownHeight && spaceAbove > spaceBelow) {
        setOpenUpward(true);
      } else {
        setOpenUpward(false);
      }

      const dropdownEl = dropdownRef.current;
      const dropdownRect = dropdownEl.getBoundingClientRect();
      const dropdownWidthPx = dropdownRect.width || 200;
      const viewportWidth = window.innerWidth;
      const padding = 8;

      if (alignRight) {
        const rightEdge = buttonRect.right;
        const leftPosition = rightEdge - dropdownWidthPx;
        if (leftPosition < padding) {
          setHorizontalPosition('left');
          setHorizontalOffset(0);
        } else {
          setHorizontalPosition('right');
          setHorizontalOffset(0);
        }
      } else {
        const leftEdge = buttonRect.left;
        const rightPosition = leftEdge + dropdownWidthPx;
        if (rightPosition > viewportWidth - padding) {
          if (buttonRect.right - dropdownWidthPx >= padding) {
            setHorizontalPosition('right');
            setHorizontalOffset(0);
          } else {
            setHorizontalPosition('center');
            const centerOffset = (buttonRect.width - dropdownWidthPx) / 2;
            setHorizontalOffset(centerOffset);
          }
        } else {
          setHorizontalPosition('left');
          setHorizontalOffset(0);
        }
      }
    }
  }, [isOpen, alignRight]);

  // Close on outside click
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (
        dropdownRef.current &&
        !dropdownRef.current.contains(event.target as Node) &&
        buttonRef.current &&
        !buttonRef.current.contains(event.target as Node)
      ) {
        setIsOpen(false);
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
    }
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen]);

  // Close on escape
  useEffect(() => {
    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };
    if (isOpen) {
      document.addEventListener('keydown', handleEscape);
    }
    return () => document.removeEventListener('keydown', handleEscape);
  }, [isOpen]);

  const handleToggleOption = (optionValue: string) => {
    const isSelected = values.includes(optionValue);

    if (isSelected) {
      // Don't allow deselection if at minimum
      if (selectedCount <= minSelections) return;
      onChange(values.filter((v) => v !== optionValue));
    } else {
      // Don't allow selection if at maximum
      if (maxSelections && selectedCount >= maxSelections) return;
      onChange([...values, optionValue]);
    }
  };

  const getDropdownPosition = () => {
    if (!buttonRef.current) return { top: 0, left: 0 };
    const rect = buttonRef.current.getBoundingClientRect();

    let left = rect.left + horizontalOffset;
    if (horizontalPosition === 'right') {
      const dropdownEl = dropdownRef.current;
      const dropdownWidthPx = dropdownEl?.getBoundingClientRect().width || 200;
      left = rect.right - dropdownWidthPx;
    }

    return {
      top: openUpward ? rect.top - 4 : rect.bottom + 4,
      left
    };
  };

  const position = getDropdownPosition();

  return (
    <>
      <style>{`
        @keyframes multiSelectSlide {
          from {
            opacity: 0;
            transform: translateY(-8px) scale(0.98);
          }
          to {
            opacity: 1;
            transform: translateY(0) scale(1);
          }
        }
        @keyframes multiSelectSlideUp {
          from {
            opacity: 0;
            transform: translateY(8px) scale(0.98);
          }
          to {
            opacity: 1;
            transform: translateY(0) scale(1);
          }
        }
      `}</style>
      <div className={`relative ${className}`}>
        <button
        ref={buttonRef}
        type="button"
        onClick={() => !disabled && setIsOpen(!isOpen)}
        disabled={disabled}
        className={`w-full px-3 py-2 rounded-lg border text-[var(--theme-text-primary)] text-left transition-all flex items-center justify-between text-sm focus:outline-none focus:ring-0 focus:shadow-none active:outline-none active:ring-0 ${disabled ? 'opacity-50 cursor-not-allowed' : ''}`}
        style={{
          backgroundColor: 'var(--theme-card-bg)',
          borderColor: isOpen ? 'var(--theme-border-focus)' : 'var(--theme-border-primary)',
          outline: 'none',
          boxShadow: 'none',
          WebkitTapHighlightColor: 'transparent'
        }}
        onMouseEnter={(e) =>
          !disabled && (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
        }
        onMouseLeave={(e) =>
          !disabled && (e.currentTarget.style.backgroundColor = 'var(--theme-card-bg)')
        }
      >
        <span className="truncate" style={{ color: 'var(--theme-text-primary)' }}>
          {getDisplayLabel()}
        </span>
        <ChevronDown
          className={`w-4 h-4 flex-shrink-0 transition-transform duration-200 ${isOpen ? 'rotate-180' : ''}`}
          style={{ color: 'var(--theme-text-primary)' }}
        />
      </button>

      {isOpen &&
        createPortal(
          <div
            ref={dropdownRef}
            className={`fixed z-[200000] ${dropdownWidth || 'w-64'} rounded-lg overflow-hidden`}
            style={{
              top: openUpward ? 'auto' : position.top,
              bottom: openUpward ? window.innerHeight - position.top : 'auto',
              left: position.left,
              backgroundColor: 'var(--theme-bg-secondary)',
              border: '1px solid var(--theme-border-primary)',
              boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.3), 0 8px 10px -6px rgba(0, 0, 0, 0.2), 0 0 0 1px rgba(0, 0, 0, 0.1)',
              animation: openUpward ? 'multiSelectSlideUp 0.15s cubic-bezier(0.16, 1, 0.3, 1)' : 'multiSelectSlide 0.15s cubic-bezier(0.16, 1, 0.3, 1)'
            }}
          >
            {title && (
              <div
                className="px-3 py-2 text-sm font-medium border-b"
                style={{
                  color: 'var(--theme-text-secondary)',
                  borderColor: 'var(--theme-border-primary)',
                  backgroundColor: 'var(--theme-bg-secondary)'
                }}
              >
                {title}
              </div>
            )}
            <CustomScrollbar maxHeight="240px" paddingMode="compact">
              <div className="py-1">
                {options.map((option) => {
                  const isSelected = values.includes(option.value);
                  const Icon = option.icon;
                  const canDeselect = selectedCount > minSelections;
                  const canSelect = !maxSelections || selectedCount < maxSelections;
                  const isDisabled = option.disabled || (isSelected && !canDeselect) || (!isSelected && !canSelect);

                  return (
                    <button
                      key={option.value}
                      type="button"
                      onClick={() => !isDisabled && handleToggleOption(option.value)}
                      disabled={isDisabled}
                      className={`w-full px-3 py-2 text-left flex items-center gap-3 transition-colors ${
                        isDisabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer'
                      }`}
                      style={{
                        backgroundColor: isSelected ? 'var(--theme-bg-tertiary)' : 'transparent'
                      }}
                      onMouseEnter={(e) => {
                        if (!isDisabled) {
                          e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                        }
                      }}
                      onMouseLeave={(e) => {
                        if (!isSelected) {
                          e.currentTarget.style.backgroundColor = 'transparent';
                        } else {
                          e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)';
                        }
                      }}
                    >
                      <div
                        className={`w-4 h-4 rounded flex items-center justify-center flex-shrink-0 ${
                          isSelected ? '' : 'border'
                        }`}
                        style={{
                          backgroundColor: isSelected ? 'var(--theme-primary)' : 'transparent',
                          borderColor: isSelected ? 'transparent' : 'var(--theme-border-primary)'
                        }}
                      >
                        {isSelected && <Check className="w-3 h-3 text-white" />}
                      </div>
                      {Icon && (
                        <Icon
                          className="w-4 h-4 flex-shrink-0"
                          style={{ color: 'var(--theme-text-muted)' }}
                        />
                      )}
                      <div className="flex-1 min-w-0">
                        <div
                          className="text-sm truncate"
                          style={{ color: 'var(--theme-text-primary)' }}
                        >
                          {option.label}
                        </div>
                        {option.description && (
                          <div
                            className="text-xs truncate"
                            style={{ color: 'var(--theme-text-muted)' }}
                          >
                            {option.description}
                          </div>
                        )}
                      </div>
                    </button>
                  );
                })}
              </div>
            </CustomScrollbar>
            {minSelections > 0 && (
              <div
                className="px-3 py-2.5 text-xs border-t"
                style={{
                  color: 'var(--theme-text-secondary)',
                  borderColor: 'var(--theme-border-primary)',
                  backgroundColor: 'var(--theme-bg-tertiary)'
                }}
              >
                At least {minSelections} option{minSelections > 1 ? 's' : ''} required
              </div>
            )}
          </div>,
          document.body
        )}
      </div>
    </>
  );
};

export default MultiSelectDropdown;
