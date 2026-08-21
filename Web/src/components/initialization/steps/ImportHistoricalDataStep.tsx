import React from 'react';
import { HardDrive } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { DatabaseImportForm } from '@components/features/management/data/DatabaseImportForm';
import { StepHeader } from '@components/initialization/StepHeader';
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
      <StepHeader
        icon={<HardDrive className="w-7 h-7 icon-info" />}
        iconBackground="bg-themed-info"
        title={t('initialization.importHistorical.title')}
        description={t('initialization.importHistorical.subtitle')}
      />

      <DatabaseImportForm
        onImportComplete={handleImportComplete}
        onSkip={onSkip}
        showSkipButton={true}
      />
    </div>
  );
};
