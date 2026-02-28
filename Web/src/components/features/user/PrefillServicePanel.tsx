import React from 'react';
import { Loader2, Network } from 'lucide-react';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Alert } from '@components/ui/Alert';

interface PrefillServicePanelProps {
  serviceName: string;
  serviceNameClass: string;
  serviceIcon: React.ReactNode;
  accentClass: string;
  config: {
    enabledByDefault: boolean;
    durationHours: number;
    maxThreadCount: number | null;
  };
  onToggleEnabled: () => void;
  onDurationChange: (hours: number) => void;
  onMaxThreadsChange: (threads: number | null) => void;
  loading: boolean;
  updating: boolean;
  warningText: string;
  durationLabel: string;
  maxThreadsLabel: string;
  enableLabel: string;
  enableDescription: string;
  prefillDurationOptions: { value: string; label: string }[];
  maxThreadOptions: { value: string; label: string }[];
}

const PrefillServicePanel: React.FC<PrefillServicePanelProps> = ({
  serviceName,
  serviceNameClass,
  serviceIcon,
  accentClass,
  config,
  onToggleEnabled,
  onDurationChange,
  onMaxThreadsChange,
  loading,
  updating,
  warningText,
  durationLabel,
  maxThreadsLabel,
  enableLabel,
  enableDescription,
  prefillDurationOptions,
  maxThreadOptions
}) => {
  const handleToggleClick = () => {
    if (!loading && !updating) {
      onToggleEnabled();
    }
  };

  const handleDurationChange = (value: string) => {
    onDurationChange(Number(value));
  };

  const handleMaxThreadsChange = (value: string) => {
    const newValue = value === '' ? null : Number(value);
    onMaxThreadsChange(newValue);
  };

  return (
    <div className={`settings-group ${accentClass}`}>
      {/* Service header */}
      <div className="text-xs font-semibold uppercase tracking-wider text-themed-muted flex items-center gap-2">
        {serviceIcon}
        <span className={serviceNameClass}>{serviceName}</span>
      </div>

      {/* Enable by default toggle */}
      <div className="toggle-row cursor-pointer" onClick={handleToggleClick}>
        <div>
          <div className="toggle-row-label">{enableLabel}</div>
          <div className="toggle-row-description">{enableDescription}</div>
        </div>
        <div className="flex items-center gap-2">
          {updating && <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />}
          <div className={`modern-toggle ${config.enabledByDefault ? 'checked' : ''}`}>
            <span className="toggle-thumb" />
          </div>
        </div>
      </div>

      {/* Warning alert - only shown when enabled */}
      {config.enabledByDefault && <Alert color="yellow">{warningText}</Alert>}

      {/* Permission Duration dropdown */}
      <div className="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-4">
        <div className="toggle-row-label whitespace-nowrap">{durationLabel}</div>
        <EnhancedDropdown
          options={prefillDurationOptions}
          value={config.durationHours.toString()}
          onChange={handleDurationChange}
          disabled={updating || loading}
          className="w-48"
        />
      </div>

      {/* Max Download Threads dropdown */}
      <div className="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-4">
        <div className="toggle-row-label whitespace-nowrap flex items-center gap-1.5">
          <Network className="w-3.5 h-3.5 text-themed-accent" />
          {maxThreadsLabel}
        </div>
        <div className="relative">
          <EnhancedDropdown
            options={maxThreadOptions}
            value={config.maxThreadCount != null ? String(config.maxThreadCount) : ''}
            onChange={handleMaxThreadsChange}
            disabled={updating || loading}
            className="w-48"
          />
          {updating && (
            <Loader2 className="w-4 h-4 animate-spin absolute right-10 top-1/2 -translate-y-1/2 text-themed-accent" />
          )}
        </div>
      </div>
    </div>
  );
};

export default PrefillServicePanel;
