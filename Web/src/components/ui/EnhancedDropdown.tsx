import React, { useState, useEffect, useRef } from 'react';
import { ChevronDown } from 'lucide-react';

export interface DropdownOption {
  value: string;
  label: string;
}

interface EnhancedDropdownProps {
  options: DropdownOption[];
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  className?: string;
  disabled?: boolean;
}

export const EnhancedDropdown: React.FC<EnhancedDropdownProps> = ({
  options,
  value,
  onChange,
  placeholder = 'Select option',
  className = '',
  disabled = false
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  const selectedOption = options.find((opt) => opt.value === value);

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
    <div className={`relative ${className}`}>
      <button
        ref={buttonRef}
        type="button"
        onClick={() => !disabled && setIsOpen(!isOpen)}
        disabled={disabled}
        className={`w-full px-3 py-2 rounded-lg border text-[var(--theme-text-primary)] text-left focus:outline-none transition-colors flex items-center justify-between text-sm ${disabled ? 'opacity-50 cursor-not-allowed' : ''}`}
        style={{
          backgroundColor: 'var(--theme-bg-secondary)',
          borderColor: 'var(--theme-border-primary)'
        }}
        onMouseEnter={(e) =>
          !disabled && (e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)')
        }
        onMouseLeave={(e) =>
          !disabled && (e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)')
        }
      >
        <span className="truncate">{selectedOption ? selectedOption.label : placeholder}</span>
        <ChevronDown size={16} className={`transition-transform ${isOpen ? 'rotate-180' : ''}`} />
      </button>

      {isOpen && (
        <div
          ref={dropdownRef}
          className="absolute mt-1 w-full rounded-lg border shadow-xl z-[9999] overflow-x-hidden"
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            borderColor: 'var(--theme-border-primary)',
            maxHeight: '240px',
            overflowY: 'auto',
            maxWidth: '100vw'
          }}
        >
          <div className="py-1">
            {options.map((option) => (
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
                  onClick={() => handleSelect(option.value)}
                  className={`w-full px-4 py-2 text-left text-sm hover:bg-[var(--theme-bg-tertiary)] transition-colors truncate ${
                    option.value === value
                      ? 'bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-primary)]'
                      : 'text-[var(--theme-text-secondary)]'
                  } ${
                    options.findIndex(opt => opt.value === 'divider') !== -1 &&
                    options.findIndex(opt => opt.value === option.value) > options.findIndex(opt => opt.value === 'divider')
                      ? 'opacity-75 text-xs pl-6'
                      : ''
                  }`}
                  title={option.label}
                >
                  {option.label}
                </button>
              )
            ))}
          </div>
        </div>
      )}
    </div>
  );
};