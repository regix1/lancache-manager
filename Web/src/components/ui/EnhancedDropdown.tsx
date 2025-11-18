import React, { useState, useEffect, useRef } from 'react';
import { ChevronDown, Check } from 'lucide-react';

export interface DropdownOption {
  value: string;
  label: string;
  shortLabel?: string; // Compact label for button display
  description?: string;
  icon?: React.ComponentType<any>;
  disabled?: boolean;
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
  dropdownWidth?: string; // Custom width for dropdown menu (e.g., 'w-64', '16rem')
  alignRight?: boolean; // When true, dropdown aligns to the right of the button
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
  dropdownWidth,
  alignRight = false
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const [openUpward, setOpenUpward] = useState(false);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  const selectedOption = options.find((opt) => opt.value === value);

  // Check if dropdown should open upward
  useEffect(() => {
    if (isOpen && buttonRef.current) {
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
    }
  }, [isOpen]);

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

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setIsOpen(false);
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
      document.addEventListener('keydown', handleEscape);
      return () => {
        document.removeEventListener('mousedown', handleClickOutside);
        document.removeEventListener('keydown', handleEscape);
      };
    }
  }, [isOpen]);

  const handleSelect = (optionValue: string) => {
    onChange(optionValue);
    setIsOpen(false);
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
              ? (compactMode && selectedOption.shortLabel
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
          className={`absolute ${dropdownWidth || 'w-full'} ${alignRight ? 'right-0' : 'left-0'} rounded-lg border z-[9999] overflow-x-hidden ${
            openUpward ? 'bottom-full mb-2' : 'mt-2'
          }`}
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            borderColor: 'var(--theme-border-primary)',
            maxHeight: '280px',
            overflowY: 'auto',
            maxWidth: '100vw',
            boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.2), 0 8px 10px -6px rgba(0, 0, 0, 0.15), 0 0 0 1px rgba(0, 0, 0, 0.05)',
            animation: openUpward ? 'dropdownSlideUp 0.15s cubic-bezier(0.16, 1, 0.3, 1)' : 'dropdownSlide 0.15s cubic-bezier(0.16, 1, 0.3, 1)'
          }}
        >
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
                    {option.icon && (
                      <option.icon
                        className="flex-shrink-0 mt-0.5"
                        size={16}
                        style={{ color: option.value === value ? 'var(--theme-primary)' : 'var(--theme-text-secondary)' }}
                      />
                    )}
                    <div className="flex flex-col flex-1 min-w-0">
                      <span className={`font-medium truncate ${option.value === value ? 'text-[var(--theme-text-primary)]' : 'text-[var(--theme-text-secondary)]'}`}>
                        {option.label}
                      </span>
                      {option.description && (
                        <span className="text-xs text-[var(--theme-text-secondary)] mt-0.5 leading-relaxed">
                          {option.description}
                        </span>
                      )}
                    </div>
                    {option.value === value && (
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
        </div>
      )}
      </div>
    </>
  );
};
