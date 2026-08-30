import React from 'react';
import { useTranslation } from 'react-i18next';
import { Tooltip } from '@components/ui/Tooltip';

interface ScanWhileDownloadingGateProps {
  /** Whether a download is currently writing into the cache. */
  blocked: boolean;
  position?: 'top' | 'bottom' | 'left' | 'right';
  /** Trigger wrapper class. Full-width menu items pass "block w-full"; inline buttons keep the default. */
  className?: string;
  children: React.ReactNode;
}

/**
 * Wraps a cache scan action while a download is in flight. The caller disables the control and
 * this adds the hover explanation; when nothing is downloading the child renders untouched so
 * normal controls gain no extra tooltip.
 */
export const ScanWhileDownloadingGate: React.FC<ScanWhileDownloadingGateProps> = ({
  blocked,
  position = 'left',
  className = 'block w-full',
  children
}) => {
  const { t } = useTranslation();

  if (!blocked) {
    return <>{children}</>;
  }

  return (
    <Tooltip
      content={t('management.gameDetection.blockedWhileDownloading')}
      position={position}
      className={className}
    >
      {children}
    </Tooltip>
  );
};
