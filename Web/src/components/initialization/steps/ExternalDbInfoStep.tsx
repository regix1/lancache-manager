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

  const isExternal = setupStatus?.mode === 'external';
  const host =
    setupStatus?.postgresHost ??
    (isExternal ? t('initialization.dbInfo.unknown', '(unknown)') : '/var/run/postgresql');
  const port = setupStatus?.postgresPort;
  const database = setupStatus?.postgresDatabase ?? 'lancache';
  const user = setupStatus?.postgresUser ?? 'lancache';

  return (
    <div className="space-y-5">
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-success">
          <CheckCircle className="w-7 h-7 icon-success" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">
          {isExternal
            ? t('initialization.dbInfo.externalTitle', 'External PostgreSQL Connected')
            : t('initialization.dbInfo.embeddedTitle', 'Embedded PostgreSQL Ready')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {isExternal
            ? t(
                'initialization.dbInfo.externalBody',
                'This container is configured to use an external PostgreSQL server. The connection details below come from your environment variables (or a saved credentials file).'
              )
            : t(
                'initialization.dbInfo.embeddedBody',
                'This container uses the bundled PostgreSQL database over a local Unix socket. Credentials are already configured via environment variables or the setup wizard.'
              )}
        </p>
      </div>

      <div className="rounded-lg border border-themed-secondary bg-themed-tertiary p-4 space-y-2">
        <div className="flex items-center gap-2 text-sm font-semibold text-themed-primary">
          <Database className="w-4 h-4 icon-primary" />
          {t('initialization.dbInfo.target', 'Connection target')}
        </div>
        <dl className="text-sm grid grid-cols-3 gap-y-1">
          <dt className="col-span-1 text-themed-secondary">
            {isExternal
              ? t('initialization.dbInfo.host', 'Host')
              : t('initialization.dbInfo.socket', 'Socket')}
          </dt>
          <dd className="col-span-2 text-themed-primary font-mono break-all">{host}</dd>
          {isExternal && port != null && (
            <>
              <dt className="col-span-1 text-themed-secondary">
                {t('initialization.dbInfo.port', 'Port')}
              </dt>
              <dd className="col-span-2 text-themed-primary font-mono">{port}</dd>
            </>
          )}
          <dt className="col-span-1 text-themed-secondary">
            {t('initialization.dbInfo.database', 'Database')}
          </dt>
          <dd className="col-span-2 text-themed-primary font-mono">{database}</dd>
          <dt className="col-span-1 text-themed-secondary">
            {t('initialization.dbInfo.user', 'User')}
          </dt>
          <dd className="col-span-2 text-themed-primary font-mono">{user}</dd>
        </dl>
      </div>

      <Button variant="default" onClick={onContinue} className="w-full">
        {t('initialization.dbInfo.continue', 'Continue')}
        <ArrowRight className="w-4 h-4 ml-1" />
      </Button>
    </div>
  );
};
