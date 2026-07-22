import React from 'react';
import { useTranslation } from 'react-i18next';
import { Network } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { Alert } from '@components/ui/Alert';
import Badge from '@components/ui/Badge';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { HelpPopover, HelpNote } from '@components/ui/HelpPopover';

type PrefillServiceIcon = React.ComponentType<{
  size?: number;
  className?: string;
  style?: React.CSSProperties;
}>;

interface PrefillServicePanelProps {
  serviceName: string;
  serviceIcon: PrefillServiceIcon;
  iconColor: string;
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
  durationHelpText: string;
  enableLabel: string;
  enableDescription: string;
  prefillDurationOptions: { value: string; label: string }[];
  /** When false, the max download threads control is hidden (e.g. anonymous Battle.net). Defaults to true. */
  showMaxThreads?: boolean;
  maxThreadsLabel?: string;
  maxThreadOptions?: { value: string; label: string }[];
  isExpanded: boolean;
  onToggle: () => void;
}

const PrefillServicePanel: React.FC<PrefillServicePanelProps> = ({
  serviceName,
  serviceIcon: ServiceIcon,
  iconColor,
  config,
  onToggleEnabled,
  onDurationChange,
  onMaxThreadsChange,
  loading,
  updating,
  warningText,
  durationLabel,
  durationHelpText,
  enableLabel,
  enableDescription,
  prefillDurationOptions,
  showMaxThreads = true,
  maxThreadsLabel,
  maxThreadOptions,
  isExpanded,
  onToggle
}) => {
  const { t } = useTranslation();

  const handleDurationChange = (value: string) => {
    onDurationChange(Number(value));
  };

  const handleMaxThreadsChange = (value: string) => {
    const newValue = value === '' ? null : Number(value);
    onMaxThreadsChange(newValue);
  };

  return (
    <AccordionSection
      title={serviceName}
      description={enableDescription}
      icon={ServiceIcon}
      iconColor={iconColor}
      surface="well"
      isExpanded={isExpanded}
      onToggle={onToggle}
      badge={
        <Badge variant={config.enabledByDefault ? 'success' : 'neutral'}>
          {config.enabledByDefault
            ? t('management.sections.settings.enabled')
            : t('management.sections.settings.disabled')}
        </Badge>
      }
    >
      <div className="mgmt-list divided-list user-settings-list">
        <div className="mgmt-row">
          <div className="mgmt-row__body">
            <p className="mgmt-row__title">{enableLabel}</p>
            <p className="mgmt-row__meta">{enableDescription}</p>
          </div>
          <div className="mgmt-row__actions">
            <ToggleSwitch
              options={[
                { value: 'off', label: t('common.off') },
                { value: 'on', label: t('common.on'), activeColor: 'success' }
              ]}
              value={config.enabledByDefault ? 'on' : 'off'}
              onChange={() => onToggleEnabled()}
              disabled={loading || updating}
              size="sm"
            />
          </div>
        </div>
      </div>

      <div className="user-settings-detail-stack user-settings-service-body">
        {config.enabledByDefault && <Alert color="yellow">{warningText}</Alert>}

        <div className="user-settings-field-row">
          <span className="user-settings-field-label">
            {durationLabel}
            <HelpPopover position="left" width={280}>
              <HelpNote type="info">{durationHelpText}</HelpNote>
            </HelpPopover>
          </span>
          <span className="user-settings-dropdown">
            <EnhancedDropdown
              options={prefillDurationOptions}
              value={config.durationHours.toString()}
              onChange={handleDurationChange}
              disabled={updating || loading}
              size="md"
              className="w-40"
            />
          </span>
        </div>

        {showMaxThreads && maxThreadOptions && (
          <div className="user-settings-field-row">
            <span className="user-settings-field-label">
              <Network className="w-3.5 h-3.5 text-themed-accent" />
              {maxThreadsLabel}
            </span>
            <span className="user-settings-dropdown">
              <EnhancedDropdown
                options={maxThreadOptions}
                value={config.maxThreadCount != null ? String(config.maxThreadCount) : ''}
                onChange={handleMaxThreadsChange}
                disabled={updating || loading}
                size="md"
                className="w-40"
              />
              {updating && (
                <LoadingSpinner inline size="sm" className="user-settings-inline-spinner" />
              )}
            </span>
          </div>
        )}
      </div>
    </AccordionSection>
  );
};

export default PrefillServicePanel;
