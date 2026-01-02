import React, { useState, useLayoutEffect, useEffect, useRef, useMemo, useCallback, memo } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, Check } from 'lucide-react';

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

// Memoized option component
interface OptionItemProps {
  option: MultiSelectOption;
  isSelected: boolean;
  isDisabled: boolean;
  isLast: boolean;
  onToggle: (value: string) => void;
}

const OptionItem = memo<OptionItemProps>(({ option, isSelected, isDisabled, isLast, onToggle }) => {
  const Icon = option.icon;

  return (
    <button
      type="button"
      onClick={() => !isDisabled && onToggle(option.value)}
      disabled={isDisabled}
      className={`
        msd-option w-full text-left flex items-start gap-3
        ${isSelected ? 'msd-option-selected' : ''}
        ${isDisabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'}
        ${!isLast ? 'border-b' : ''}
      `}
      style={{
        padding: '14px 16px',
        borderColor: 'var(--theme-border-secondary)',
        backgroundColor: 'var(--theme-bg-secondary)'
      }}
    >
      <div
        className="msd-accent absolute left-0 top-0 bottom-0 w-0.5"
        style={{ backgroundColor: 'var(--theme-primary)' }}
      />

      <div
        className={`msd-checkbox flex-shrink-0 w-5 h-5 rounded-md flex items-center justify-center mt-0.5 ${isSelected ? 'msd-checkbox-selected' : ''}`}
        style={{
          backgroundColor: isSelected ? 'var(--theme-primary)' : 'transparent',
          border: isSelected ? 'none' : '2px solid var(--theme-border-primary)',
          boxShadow: isSelected ? '0 2px 4px color-mix(in srgb, var(--theme-primary) 30%, transparent)' : 'none'
        }}
      >
        <Check className="msd-checkbox-inner w-3.5 h-3.5" style={{ color: 'white' }} strokeWidth={3} />
      </div>

      {Icon && (
        <div
          className="flex-shrink-0 mt-0.5"
          style={{ color: isSelected ? 'var(--theme-primary)' : 'var(--theme-text-muted)' }}
        >
          <Icon size={18} />
        </div>
      )}

      <div className="flex-1 min-w-0">
        <div className="text-sm font-medium leading-tight" style={{ color: 'var(--theme-text-primary)' }}>
          {option.label}
        </div>
        {option.description && (
          <div className="text-xs leading-relaxed mt-1" style={{ color: 'var(--theme-text-muted)', lineHeight: '1.4' }}>
            {option.description}
          </div>
        )}
      </div>
    </button>
  );
});

OptionItem.displayName = 'OptionItem';

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
  const [dropdownStyle, setDropdownStyle] = useState<{ top?: number; bottom?: number; left: number; animation: string }>({ left: 0, animation: '' });
  const dropdownRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  const valuesSet = useMemo(() => new Set(values), [values]);
  const selectedCount = valuesSet.size;

  const displayLabel = useMemo(() => {
    if (selectedCount === 0) return placeholder;
    if (selectedCount === 1) {
      const opt = options.find(o => valuesSet.has(o.value));
      return opt?.label || placeholder;
    }
    if (selectedCount === options.length) return 'All selected';
    return `${selectedCount} selected`;
  }, [selectedCount, options, valuesSet, placeholder]);

  // Calculate position before paint
  useLayoutEffect(() => {
    if (!isOpen || !buttonRef.current) return;

    const rect = buttonRef.current.getBoundingClientRect();
    const dropdownHeight = 300;
    const spaceBelow = window.innerHeight - rect.bottom;
    const openUpward = spaceBelow < dropdownHeight && rect.top > spaceBelow;

    const dropdownWidthPx = dropdownRef.current?.getBoundingClientRect().width || 200;
    let left = rect.left;

    if (alignRight) {
      const pos = rect.right - dropdownWidthPx;
      if (pos >= 8) left = pos;
    } else if (rect.left + dropdownWidthPx > window.innerWidth - 8) {
      left = rect.right - dropdownWidthPx >= 8 ? rect.right - dropdownWidthPx : rect.left + (rect.width - dropdownWidthPx) / 2;
    }

    setDropdownStyle(openUpward
      ? { bottom: window.innerHeight - rect.top + 4, left, animation: 'msdFadeInUp 0.18s cubic-bezier(0.16, 1, 0.3, 1) forwards' }
      : { top: rect.bottom + 4, left, animation: 'msdFadeInDown 0.18s cubic-bezier(0.16, 1, 0.3, 1) forwards' }
    );
  }, [isOpen, alignRight]);

  // Combined event listeners
  useEffect(() => {
    if (!isOpen) return;

    const handleClickOutside = (e: MouseEvent) => {
      if (!dropdownRef.current?.contains(e.target as Node) && !buttonRef.current?.contains(e.target as Node)) {
        setIsOpen(false);
      }
    };

    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };

    document.addEventListener('mousedown', handleClickOutside);
    document.addEventListener('keydown', handleEscape);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
      document.removeEventListener('keydown', handleEscape);
    };
  }, [isOpen]);

  const handleToggle = useCallback((optionValue: string) => {
    const isSelected = valuesSet.has(optionValue);
    if (isSelected) {
      if (selectedCount > minSelections) {
        onChange(values.filter(v => v !== optionValue));
      }
    } else if (!maxSelections || selectedCount < maxSelections) {
      onChange([...values, optionValue]);
    }
  }, [valuesSet, selectedCount, minSelections, maxSelections, onChange, values]);

  const canDeselect = selectedCount > minSelections;
  const canSelect = !maxSelections || selectedCount < maxSelections;

  return (
    <div className={`relative ${className}`}>
      <button
        ref={buttonRef}
        type="button"
        onClick={() => !disabled && setIsOpen(!isOpen)}
        disabled={disabled}
        className={`w-full px-3 py-2.5 rounded-lg border text-left transition-all duration-150 flex items-center justify-between gap-2 text-sm font-medium ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer hover:border-[var(--theme-border-focus)]'}`}
        style={{
          backgroundColor: 'var(--theme-card-bg)',
          borderColor: isOpen ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
          color: 'var(--theme-text-primary)',
          boxShadow: isOpen ? '0 0 0 3px color-mix(in srgb, var(--theme-primary) 15%, transparent)' : 'none'
        }}
      >
        <span className="truncate flex-1">{displayLabel}</span>
        <div className="flex items-center gap-1.5" style={{ color: 'var(--theme-text-muted)' }}>
          {selectedCount > 0 && (
            <span className="text-xs px-1.5 py-0.5 rounded-full font-semibold" style={{ backgroundColor: 'var(--theme-primary)', color: 'white' }}>
              {selectedCount}
            </span>
          )}
          <ChevronDown className={`w-4 h-4 flex-shrink-0 transition-transform duration-200 ${isOpen ? 'rotate-180' : ''}`} />
        </div>
      </button>

      {isOpen && createPortal(
        <div
          ref={dropdownRef}
          className={`msd-dropdown fixed z-[200000] ${dropdownWidth || 'w-72'} rounded-xl overflow-hidden`}
          style={{
            top: dropdownStyle.top,
            bottom: dropdownStyle.bottom,
            left: dropdownStyle.left,
            backgroundColor: 'var(--theme-bg-secondary)',
            border: '1px solid var(--theme-border-primary)',
            boxShadow: '0 4px 6px -1px rgba(0,0,0,0.1), 0 10px 20px -5px rgba(0,0,0,0.2), 0 20px 40px -10px rgba(0,0,0,0.15), inset 0 1px 0 0 color-mix(in srgb, white 5%, transparent)',
            animation: dropdownStyle.animation
          }}
        >
          {title && (
            <div
              className="px-4 py-3 text-xs font-semibold uppercase tracking-wider border-b"
              style={{ color: 'var(--theme-text-muted)', borderColor: 'var(--theme-border-secondary)', backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              {title}
            </div>
          )}

          <div className="overflow-y-auto" style={{ maxHeight: '280px', backgroundColor: 'var(--theme-bg-secondary)' }}>
            {options.map((option, i) => (
              <OptionItem
                key={option.value}
                option={option}
                isSelected={valuesSet.has(option.value)}
                isDisabled={option.disabled || (valuesSet.has(option.value) && !canDeselect) || (!valuesSet.has(option.value) && !canSelect)}
                isLast={i === options.length - 1}
                onToggle={handleToggle}
              />
            ))}
          </div>

          {minSelections > 0 && (
            <div
              className="px-4 py-3 text-xs border-t flex items-center gap-2"
              style={{ color: 'var(--theme-text-muted)', borderColor: 'var(--theme-border-secondary)', backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <div className="w-1.5 h-1.5 rounded-full" style={{ backgroundColor: 'var(--theme-warning)' }} />
              <span>Minimum {minSelections} selection{minSelections > 1 ? 's' : ''} required</span>
            </div>
          )}
        </div>,
        document.body
      )}
    </div>
  );
};

export default MultiSelectDropdown;
