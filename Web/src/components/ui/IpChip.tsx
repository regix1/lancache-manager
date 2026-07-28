import React from 'react';
import { X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Tooltip } from './Tooltip';
import type { IpChipState } from './IpChip.types';

interface IpChipProps {
  address: string;
  state: IpChipState;
  /** Omit for a glance-only chip; no remove control is rendered without it. */
  onRemove?: () => void;
  /** Tooltip and screen-reader wording for the remove control. Defaults to "Remove". */
  removeLabel?: string;
  /**
   * The whole accessible name for the remove control. Supply it where the label is prose, which
   * reads as a sentence fragment once a verb is put in front of it. [54]
   */
  removeAriaLabel?: string;
  /** Blocks the remove control while a save is in flight. */
  disabled?: boolean;
  /**
   * Set false where the chip carries a readable label instead of an address, so the digit
   * alignment a monospace face buys on an address is not spent on prose. [39]
   */
  mono?: boolean;
  className?: string;
}

export const IpChip: React.FC<IpChipProps> = ({
  address,
  state,
  onRemove,
  removeLabel,
  removeAriaLabel,
  disabled = false,
  mono = true,
  className = ''
}) => {
  const { t } = useTranslation();
  const removeText = removeLabel ?? t('common.remove');
  const removeName = removeAriaLabel ?? `${removeText} ${address}`;
  const showRemove = state !== 'readonly' && onRemove !== undefined;

  const handleRemove = (): void => {
    onRemove?.();
  };

  return (
    <div className={`ip-chip ip-chip--${state} themed-border-radius-sm focus-ring ${className}`}>
      {/* An address can be long enough to truncate, so the full value stays reachable on hover. */}
      <Tooltip content={address} position="top" className="min-w-0">
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
