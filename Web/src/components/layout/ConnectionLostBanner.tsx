import React from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { useConnectionLost } from '@hooks/useConnectionLost';

/**
 * The one app-wide notice while the live connection to the server is down. Section failure boxes
 * and chips stay quiet then, so this banner is the only place the outage shows, and the data
 * already on screen stays under it until the connection is back.
 */
const ConnectionLostBanner: React.FC = () => {
  const { t } = useTranslation();
  const connectionLost = useConnectionLost();

  if (!connectionLost) {
    return null;
  }

  return (
    <div className="container mx-auto px-4 pt-4" role="alert">
      <Alert color="error" title={t('common.errors.connectionLostTitle')}>
        <p className="text-sm">{t('common.errors.connectionLostBody')}</p>
      </Alert>
    </div>
  );
};

export default ConnectionLostBanner;
