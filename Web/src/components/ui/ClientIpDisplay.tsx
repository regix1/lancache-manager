import React from 'react';
import { useTranslation } from 'react-i18next';
import { Tooltip } from './Tooltip';
import { useClientGroups } from '@contexts/useClientGroups';
import { useClientHostnames } from '@contexts/useClientHostnames';
import { resolveClientLabel } from '@utils/clientLabel';

interface ClientIpDisplayProps {
  clientIp: string;
  className?: string;
  showTooltip?: boolean;
}

/**
 * Displays a client IP with the friendly name that stands in for it: the nickname if one exists,
 * otherwise the hostname the network's DNS server reports.
 * Shows that name as the display text with the IP in a tooltip.
 * Falls back to showing just the IP when neither is available.
 */
export const ClientIpDisplay: React.FC<ClientIpDisplayProps> = ({
  clientIp,
  className = '',
  showTooltip = true
}) => {
  const { t } = useTranslation();
  const { getGroupForIp } = useClientGroups();
  const { getHostnameForIp } = useClientHostnames();
  const group = getGroupForIp(clientIp);

  const { text: displayName, substitutesAddress } = resolveClientLabel(
    clientIp,
    group?.nickname,
    getHostnameForIp(clientIp)
  );

  if (!substitutesAddress || !showTooltip) {
    return <span className={className}>{displayName}</span>;
  }

  return (
    <Tooltip
      content={t('ui.clientIp.ipLabel', { ip: clientIp })}
      className="inline-flex self-start max-w-full min-w-0"
    >
      <span className={`cursor-help border-b border-dashed border-themed-muted ${className}`}>
        {displayName}
      </span>
    </Tooltip>
  );
};
