import React from 'react';
import { useTranslation } from 'react-i18next';
import { Cloud, Database, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';

interface DataSourceChoiceStepProps {
  onChooseGithub: () => void;
  onChooseSteam: () => void;
}

export const DataSourceChoiceStep: React.FC<DataSourceChoiceStepProps> = ({
  onChooseGithub,
  onChooseSteam
}) => {
  const { t } = useTranslation();
  
  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
          <Database className="w-7 h-7 icon-info" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">{t('initialization.dataSource.title')}</h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t('initialization.dataSource.subtitle')}
        </p>
      </div>

      {/* Info Box */}
      <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
        <p className="text-themed-secondary">
          <strong className="text-themed-primary">{t('initialization.dataSource.whatIsDepotMapping')}</strong>{' '}
          {t('initialization.dataSource.depotMappingDesc')}
        </p>
      </div>

      {/* Options Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {/* GitHub Option */}
        <div className="p-4 rounded-lg border-2 flex flex-col bg-themed-tertiary border-themed-primary">
          <div className="flex items-center gap-2 mb-2">
            <Cloud className="w-5 h-5 icon-info" />
            <h4 className="font-semibold text-themed-primary">{t('initialization.dataSource.githubData')}</h4>
          </div>
          <p className="text-sm text-themed-secondary mb-3">
            {t('initialization.dataSource.githubDesc')}
          </p>
          <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
            <li className="flex items-center gap-1.5">
              <CheckCircle className="w-3 h-3 icon-success" />
              {t('initialization.dataSource.quickSetup')}
            </li>
            <li className="flex items-center gap-1.5">
              <CheckCircle className="w-3 h-3 icon-success" />
              {t('initialization.dataSource.mappingsReady')}
            </li>
            <li className="flex items-center gap-1.5">
              <CheckCircle className="w-3 h-3 icon-success" />
              {t('initialization.dataSource.noApiKey')}
            </li>
          </ul>
          <Button
            variant="filled"
            color="blue"
            size="sm"
            onClick={onChooseGithub}
            fullWidth
          >
            {t('initialization.dataSource.useGithubData')}
          </Button>
        </div>

        {/* Steam Option */}
        <div className="p-4 rounded-lg border-2 flex flex-col bg-themed-tertiary border-themed-primary">
          <div className="flex items-center gap-2 mb-2">
            <Database className="w-5 h-5 icon-success" />
            <h4 className="font-semibold text-themed-primary">{t('initialization.dataSource.steamData')}</h4>
          </div>
          <p className="text-sm text-themed-secondary mb-3">
            {t('initialization.dataSource.steamDesc')}
          </p>
          <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
            <li className="flex items-center gap-1.5">
              <CheckCircle className="w-3 h-3 icon-success" />
              {t('initialization.dataSource.latestData')}
            </li>
            <li className="flex items-center gap-1.5">
              <CheckCircle className="w-3 h-3 icon-success" />
              {t('initialization.dataSource.playtestAccess')}
            </li>
            <li className="flex items-center gap-1.5">
              <span className="w-3 h-3 rounded-full border border-themed-secondary" />
              {t('initialization.dataSource.requiresApiKey')}
            </li>
          </ul>
          <Button
            variant="filled"
            color="green"
            size="sm"
            onClick={onChooseSteam}
            fullWidth
          >
            {t('initialization.dataSource.useSteamData')}
          </Button>
        </div>
      </div>
    </div>
  );
};
