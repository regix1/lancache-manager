import React from 'react';
import { Database, CheckCircle, ArrowRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { useSetupStatus } from '@contexts/useSetupStatus';

interface ExternalDbInfoStepProps {
  onContinue: () => void;
}

export const ExternalDbInfoStep: React.FC<ExternalDbInfoStepProps> = ({ onContinue }) => {
  const { t } = useTranslation();
  const { setupStatus } = useSetupStatus();

  const host = setupStatus?.postgresHost ?? '(unknown)';
  const port = setupStatus?.postgresPort ?? 5432;
  const database = setupStatus?.postgresDatabase ?? 'lancache';

  return (
    <div className="space-y-5">
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-success">
          <CheckCircle className="w-7 h-7 icon-success" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">
          {t('initialization.externalDbInfo.title', 'External PostgreSQL Connected')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t(
            'initialization.externalDbInfo.body',
            'This container is configured to use an external PostgreSQL server. The connection details below come from your environment variables (or a saved credentials file).'
          )}
        </p>
      </div>

      <div className="rounded-lg border border-themed-secondary bg-themed-tertiary p-4 space-y-2">
        <div className="flex items-center gap-2 text-sm font-semibold text-themed-primary">
          <Database className="w-4 h-4 icon-primary" />
          {t('initialization.externalDbInfo.target', 'Connection target')}
        </div>
        <dl className="text-sm grid grid-cols-3 gap-y-1">
          <dt className="col-span-1 text-themed-secondary">
            {t('initialization.externalDbInfo.host', 'Host')}
          </dt>
          <dd className="col-span-2 text-themed-primary font-mono">{host}</dd>
          <dt className="col-span-1 text-themed-secondary">
            {t('initialization.externalDbInfo.port', 'Port')}
          </dt>
          <dd className="col-span-2 text-themed-primary font-mono">{port}</dd>
          <dt className="col-span-1 text-themed-secondary">
            {t('initialization.externalDbInfo.database', 'Database')}
          </dt>
          <dd className="col-span-2 text-themed-primary font-mono">{database}</dd>
        </dl>
      </div>

      <Button variant="default" onClick={onContinue} className="w-full">
        {t('initialization.externalDbInfo.continue', 'Continue')}
        <ArrowRight className="w-4 h-4 ml-1" />
      </Button>
    </div>
  );
};
