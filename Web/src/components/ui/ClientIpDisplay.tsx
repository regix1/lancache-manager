import React from 'react';
import { Tooltip } from './Tooltip';
import { useClientGroups } from '@contexts/ClientGroupContext';

interface ClientIpDisplayProps {
  clientIp: string;
  className?: string;
  showTooltip?: boolean;
}

/**
 * Displays a client IP with its nickname if one exists.
 * Shows the nickname as the display text with the IP in a tooltip.
 * Falls back to showing just the IP if no nickname is assigned.
 */
export const ClientIpDisplay: React.FC<ClientIpDisplayProps> = ({
  clientIp,
  className = '',
  showTooltip = true
}) => {
  const { getGroupForIp } = useClientGroups();
  const group = getGroupForIp(clientIp);

  const displayName = group?.nickname || clientIp;
  const hasNickname = !!group?.nickname;

  if (!hasNickname || !showTooltip) {
    return <span className={className}>{displayName}</span>;
  }

  return (
    <Tooltip content={`IP: ${clientIp}`}>
      <span className={`cursor-help border-b border-dashed border-themed-muted ${className}`}>
        {displayName}
      </span>
    </Tooltip>
  );
};

/**
 * Hook to get the display name for a client IP.
 * Returns the nickname if one exists, otherwise returns the IP.
 */
export const useClientDisplayName = (clientIp: string): { displayName: string; hasNickname: boolean; nickname?: string } => {
  const { getGroupForIp } = useClientGroups();
  const group = getGroupForIp(clientIp);

  return {
    displayName: group?.nickname || clientIp,
    hasNickname: !!group?.nickname,
    nickname: group?.nickname
  };
};
