import React from 'react';
import { resolveClientLabel } from '@utils/clientLabel';
import { IpChip } from './IpChip';
import type { IpChipState } from './IpChip.types';

interface ClientAddressChipProps {
  ip: string;
  /**
   * The name to label the address with. Passed in rather than read from the hostname context,
   * because a caller that has just looked a name up needs that name to win over the reverse-name
   * map, which only covers addresses the install has already seen.
   */
  hostname: string | null;
  state: IpChipState;
  onRemove?: () => void;
  removeLabel?: string;
  disabled?: boolean;
  note?: string;
}

/**
 * One client address as a chip, labelled with its hostname where it has one and showing the raw
 * address on hover. The nickname is deliberately left out of the label: every surface that renders
 * these chips already carries the nickname as its own heading, so repeating it says nothing.
 */
export const ClientAddressChip: React.FC<ClientAddressChipProps> = ({
  ip,
  hostname,
  state,
  onRemove,
  removeLabel,
  disabled,
  note
}) => (
  <IpChip
    address={resolveClientLabel(ip, null, hostname).text}
    state={state}
    onRemove={onRemove}
    removeLabel={removeLabel}
    disabled={disabled}
    mono={false}
    tooltip={ip}
    note={note}
  />
);
