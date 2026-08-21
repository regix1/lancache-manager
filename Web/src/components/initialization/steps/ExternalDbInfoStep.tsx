import React from 'react';
import { Database, CheckCircle, ArrowRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { StepHeader } from '@components/initialization/StepHeader';
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
    (isExternal ? t('initialization.dbInfo.unknown') : '/var/run/postgresql');
  const port = setupStatus?.postgresPort;
  const database = setupStatus?.postgresDatabase ?? 'lancache';
  const user = setupStatus?.postgresUser ?? 'lancache';

  return (
    <div className="space-y-5">
      <StepHeader
        icon={<CheckCircle className="w-7 h-7 icon-success" />}
        iconBackground="bg-themed-success"
        title={
          isExternal
            ? t('initialization.dbInfo.externalTitle')
            : t('initialization.dbInfo.embeddedTitle')
        }
        description={
          isExternal
            ? t('initialization.dbInfo.externalBody')
            : t('initialization.dbInfo.embeddedBody')
        }
      />

      <div className="rounded-lg border border-themed-secondary bg-themed-tertiary p-4 space-y-2">
        <div className="flex items-center gap-2 text-sm font-semibold text-themed-primary">
          <Database className="w-4 h-4 icon-primary" />
          {t('initialization.dbInfo.target')}
        </div>
        <dl className="text-sm grid grid-cols-3 gap-y-1">
          <dt className="col-span-1 text-themed-secondary">
            {isExternal ? t('initialization.dbInfo.host') : t('initialization.dbInfo.socket')}
          </dt>
          <dd className="col-span-2 text-themed-primary font-mono break-all">{host}</dd>
          {isExternal && port != null && (
            <>
              <dt className="col-span-1 text-themed-secondary">
                {t('initialization.dbInfo.port')}
              </dt>
              <dd className="col-span-2 text-themed-primary font-mono">{port}</dd>
            </>
          )}
          <dt className="col-span-1 text-themed-secondary">
            {t('initialization.dbInfo.database')}
          </dt>
          <dd className="col-span-2 text-themed-primary font-mono">{database}</dd>
          <dt className="col-span-1 text-themed-secondary">{t('initialization.dbInfo.user')}</dt>
          <dd className="col-span-2 text-themed-primary font-mono">{user}</dd>
        </dl>
      </div>

      <Button variant="default" onClick={onContinue} className="w-full">
        {t('initialization.dbInfo.continue')}
        <ArrowRight className="w-4 h-4 ml-1" />
      </Button>
    </div>
  );
};
