import React from 'react';
import { HardDrive } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { DatabaseImportForm } from '@components/features/management/data/DatabaseImportForm';
import type { ImportResult } from '@/types/migration';

interface ImportHistoricalDataStepProps {
  onComplete: () => void;
  onSkip: () => void;
}

export const ImportHistoricalDataStep: React.FC<ImportHistoricalDataStepProps> = ({
  onComplete,
  onSkip
}) => {
  const { t } = useTranslation();

  const handleImportComplete = (_result: ImportResult) => {
    setTimeout(() => onComplete(), 2000);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
          <HardDrive className="w-7 h-7 icon-info" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">
          {t('initialization.importHistorical.title')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t('initialization.importHistorical.subtitle')}
        </p>
      </div>

      <DatabaseImportForm
        onImportComplete={handleImportComplete}
        onSkip={onSkip}
        showSkipButton={true}
      />
    </div>
  );
};
