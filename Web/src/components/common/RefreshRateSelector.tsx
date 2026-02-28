import React from 'react';
import { useTranslation } from 'react-i18next';
import { Lightbulb, Gauge, Zap, Lock } from 'lucide-react';
import { useRefreshRate } from '@contexts/RefreshRateContext';
import { type RefreshRate } from '@utils/constants';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Tooltip } from '@components/ui/Tooltip';

interface RefreshRateSelectorProps {
  disabled?: boolean;
  iconOnly?: boolean;
}

const RefreshRateSelector: React.FC<RefreshRateSelectorProps> = ({
  disabled = false,
  iconOnly = false
}) => {
  const { t } = useTranslation();
  const { refreshRate, setRefreshRate, isControlledByAdmin } = useRefreshRate();

  const refreshOptions = [
    {
      value: 'LIVE',
      label: t('common.refreshRate.options.live.label'),
      shortLabel: t('common.refreshRate.options.live.shortLabel'),
      description: t('common.refreshRate.options.live.description'),
      rightLabel: t('common.refreshRate.options.live.rightLabel'),
      icon: Zap
    },
    {
      value: 'ULTRA',
      label: t('common.refreshRate.options.ultra.label'),
      shortLabel: t('common.refreshRate.options.ultra.shortLabel'),
      description: t('common.refreshRate.options.ultra.description'),
      rightLabel: t('common.refreshRate.options.ultra.rightLabel'),
      icon: Gauge
    },
    {
      value: 'REALTIME',
      label: t('common.refreshRate.options.realtime.label'),
      shortLabel: t('common.refreshRate.options.realtime.shortLabel'),
      description: t('common.refreshRate.options.realtime.description'),
      rightLabel: t('common.refreshRate.options.realtime.rightLabel'),
      icon: Gauge
    },
    {
      value: 'STANDARD',
      label: t('common.refreshRate.options.standard.label'),
      shortLabel: t('common.refreshRate.options.standard.shortLabel'),
      description: t('common.refreshRate.options.standard.description'),
      rightLabel: t('common.refreshRate.options.standard.rightLabel'),
      icon: Gauge
    },
    {
      value: 'RELAXED',
      label: t('common.refreshRate.options.relaxed.label'),
      shortLabel: t('common.refreshRate.options.relaxed.shortLabel'),
      description: t('common.refreshRate.options.relaxed.description'),
      rightLabel: t('common.refreshRate.options.relaxed.rightLabel'),
      icon: Gauge
    },
    {
      value: 'SLOW',
      label: t('common.refreshRate.options.slow.label'),
      shortLabel: t('common.refreshRate.options.slow.shortLabel'),
      description: t('common.refreshRate.options.slow.description'),
      rightLabel: t('common.refreshRate.options.slow.rightLabel'),
      icon: Gauge
    }
  ];

  const handleRefreshRateChange = (value: string) => {
    setRefreshRate(value as RefreshRate);
  };

  const isDisabled = disabled || isControlledByAdmin;

  // If controlled by admin (guest user), show a locked indicator with tooltip
  if (isControlledByAdmin) {
    const currentOption = refreshOptions.find((opt) => opt.value === refreshRate);
    const displayLabel = currentOption?.shortLabel || refreshRate;

    return (
      <Tooltip content={t('tooltips.refreshRateControlled')}>
        <div className="flex items-center justify-center gap-1.5 px-2 py-1 rounded text-sm cursor-not-allowed opacity-75">
          <Lock className="w-3.5 h-3.5 text-themed-muted" />
          {!iconOnly && <span className="text-themed-secondary">{displayLabel}</span>}
        </div>
      </Tooltip>
    );
  }

  return (
    <EnhancedDropdown
      options={refreshOptions}
      value={refreshRate}
      onChange={handleRefreshRateChange}
      disabled={isDisabled}
      placeholder={t('common.refreshRate.placeholder')}
      compactMode={true}
      dropdownWidth="w-64"
      alignRight={true}
      dropdownTitle={t('common.refreshRate.title')}
      footerNote={t('common.refreshRate.footerNote')}
      footerIcon={Lightbulb}
      cleanStyle={true}
      iconOnly={iconOnly}
      triggerAriaLabel={t('common.refreshRate.title')}
    />
  );
};

export default RefreshRateSelector;
