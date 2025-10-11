import React, { useState, useRef, useEffect } from 'react';
import { Gauge, ChevronDown } from 'lucide-react';
import { usePollingRate } from '@contexts/PollingRateContext';
import { POLLING_RATES, type PollingRate } from '@utils/constants';
import { Tooltip } from './Tooltip';

interface PollingRateSelectorProps {
  disabled?: boolean;
}

const PollingRateSelector: React.FC<PollingRateSelectorProps> = ({ disabled = false }) => {
  const { pollingRate, setPollingRate } = usePollingRate();
  const [showDropdown, setShowDropdown] = useState(false);
  const dropdownRef = useRef<HTMLDivElement>(null);

  const pollingOptions: {
    value: PollingRate;
    label: string;
    shortLabel: string;
    description: string;
  }[] = [
    {
      value: 'ULTRA',
      label: 'Ultra-fast',
      shortLabel: '1s',
      description: 'Updates every 1 second (very high load, unstable)'
    },
    {
      value: 'REALTIME',
      label: 'Real-time',
      shortLabel: '5s',
      description: 'Updates every 5 seconds (high server load)'
    },
    {
      value: 'STANDARD',
      label: 'Standard',
      shortLabel: '10s',
      description: 'Updates every 10 seconds (recommended)'
    },
    {
      value: 'RELAXED',
      label: 'Relaxed',
      shortLabel: '30s',
      description: 'Updates every 30 seconds (low server load)'
    },
    {
      value: 'SLOW',
      label: 'Slow',
      shortLabel: '60s',
      description: 'Updates every 60 seconds (minimal server impact)'
    }
  ];

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(event.target as Node)) {
        setShowDropdown(false);
      }
    };

    if (showDropdown) {
      document.addEventListener('mousedown', handleClickOutside);
      return () => document.removeEventListener('mousedown', handleClickOutside);
    }
  }, [showDropdown]);

  const handlePollingRateChange = (value: PollingRate) => {
    setPollingRate(value);
    setShowDropdown(false);
  };

  const getCurrentLabel = () => {
    const option = pollingOptions.find(opt => opt.value === pollingRate);
    return option?.shortLabel || '10s';
  };

  const getCurrentTooltip = () => {
    const option = pollingOptions.find(opt => opt.value === pollingRate);
    return `Polling Rate: ${option?.description || 'Updates every 10 seconds'}`;
  };

  return (
    <div className="relative" ref={dropdownRef}>
      <Tooltip content={disabled ? 'Polling rate is disabled in mock mode' : getCurrentTooltip()}>
        <button
          onClick={() => !disabled && setShowDropdown(!showDropdown)}
          disabled={disabled}
          className="flex items-center gap-1.5 px-2 sm:px-3 py-1.5 rounded-lg transition-all"
          style={{
            backgroundColor: showDropdown ? 'var(--theme-bg-tertiary)' : 'var(--theme-bg-secondary)',
            border: showDropdown ? '1px solid var(--theme-primary)' : '1px solid var(--theme-border-primary)',
            opacity: disabled ? 0.5 : 1,
            cursor: disabled ? 'not-allowed' : 'pointer'
          }}
        >
          <Gauge className="w-4 h-4 text-[var(--theme-primary)]" />
          <span className="text-xs sm:text-sm font-medium text-[var(--theme-text-primary)]">
            {getCurrentLabel()}
          </span>
          <ChevronDown
            className={`w-3 h-3 text-[var(--theme-text-secondary)] transition-transform ${
              showDropdown ? 'rotate-180' : ''
            }`}
          />
        </button>
      </Tooltip>

      {showDropdown && (
        <div
          className="absolute right-0 mt-2 w-64 rounded-lg shadow-xl z-[99999]"
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            border: '1px solid var(--theme-border-primary)'
          }}
        >
          <div className="p-1">
            <div className="px-2 py-1.5 text-xs font-semibold text-[var(--theme-text-secondary)]">
              Polling Rate
            </div>
            {pollingOptions.map((option) => (
              <button
                key={option.value}
                onClick={() => handlePollingRateChange(option.value)}
                className={`
                  w-full px-3 py-2 rounded text-sm transition-colors text-left
                  ${pollingRate === option.value
                    ? 'bg-[var(--theme-primary)]/10 text-[var(--theme-primary)]'
                    : 'text-[var(--theme-text-primary)] hover:bg-[var(--theme-bg-tertiary)]'
                  }
                `}
              >
                <div className="flex items-center justify-between">
                  <div className="flex flex-col">
                    <span className="font-medium">{option.label}</span>
                    <span className="text-xs text-[var(--theme-text-secondary)] mt-0.5">
                      {option.description}
                    </span>
                  </div>
                  <span className="ml-2 text-xs font-mono opacity-60">
                    {option.shortLabel}
                  </span>
                </div>
              </button>
            ))}
          </div>
          <div
            className="px-3 py-2 text-xs text-[var(--theme-text-secondary)] border-t"
            style={{ borderColor: 'var(--theme-border-primary)' }}
          >
            💡 Lower rates reduce server load but data may be less current
          </div>
        </div>
      )}
    </div>
  );
};

export default PollingRateSelector;
