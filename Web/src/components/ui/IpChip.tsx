import React from 'react';
import { X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Tooltip } from './Tooltip';
import type { IpChipProps } from './IpChip.types';

export const IpChip: React.FC<IpChipProps> = ({
  address,
  state,
  onRemove,
  removeLabel,
  removeAriaLabel,
  disabled = false,
  mono = true,
  tooltip,
  className = ''
}) => {
  const { t } = useTranslation();
  const removeText = removeLabel ?? t('common.remove');
  const removeName = removeAriaLabel ?? `${removeText} ${address}`;
  const showRemove = state !== 'readonly' && onRemove !== undefined;
  const tooltipContent = tooltip ?? address;

  const handleRemove = (): void => {
    onRemove?.();
  };

  return (
    <div className={`ip-chip ip-chip--${state} themed-border-radius-sm focus-ring ${className}`}>
      {/* An address can be long enough to truncate, so the full value stays reachable on hover. */}
      <Tooltip content={tooltipContent} position="top" className="min-w-0">
        <span className={`ip-chip__label text-sm block truncate${mono ? ' font-mono' : ''}`}>
          {address}
        </span>
      </Tooltip>
      {showRemove && (
        <Tooltip content={removeText} position="top" className="flex items-center flex-shrink-0">
          <button
            type="button"
            onClick={handleRemove}
            disabled={disabled}
            aria-label={removeName}
            className="ip-chip__remove pointer-target-44 focus-ring"
          >
            <X className="w-3 h-3" />
          </button>
        </Tooltip>
      )}
    </div>
  );
};
