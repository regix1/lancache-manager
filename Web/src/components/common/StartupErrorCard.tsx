import React from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';

interface StartupErrorCardProps {
  title: string;
  message: string;
  onRetry: () => void;
  children?: React.ReactNode;
}

/**
 * The full-screen card for a startup read that failed before the app can render (configuration,
 * setup status). It uses the shared red Alert, not ErrorBlock: the configuration screen renders
 * above AuthProvider, and neither screen has the connection banner above it.
 */
const StartupErrorCard: React.FC<StartupErrorCardProps> = ({
  title,
  message,
  onRetry,
  children
}) => {
  const { t } = useTranslation();
  return (
    <div className="config-error-screen">
      <div className="config-error-card">
        <Alert color="error" title={title} className="w-full text-left">
          <p className="text-sm">{message}</p>
        </Alert>
        <Button onClick={onRetry}>{t('common.retry')}</Button>
        {children}
      </div>
    </div>
  );
};

export default StartupErrorCard;
