import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Upload } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { AccordionSection } from '@components/ui/AccordionSection';
import { DatabaseImportForm } from './DatabaseImportForm';
import type { ImportResult } from '@/types/migration';

interface DataImporterProps {
  isAdmin: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
}

const DataImporter: React.FC<DataImporterProps> = ({
  isAdmin,
  mockMode,
  onSuccess,
  onDataRefresh
}) => {
  const { t } = useTranslation();
  const [isExpanded, setIsExpanded] = useState(false);

  const handleImportComplete = (result: ImportResult) => {
    onSuccess?.(
      t('management.dataImporter.messages.importCompleted', {
        imported: result.imported,
        skipped: result.skipped,
        errors: result.errors
      })
    );
    onDataRefresh?.();
  };

  return (
    <AccordionSection
      title={t('management.dataImporter.title')}
      description={t('management.dataImporter.subtitle')}
      icon={Upload}
      iconColor="var(--theme-icon-blue)"
      isExpanded={isExpanded}
      onToggle={() => setIsExpanded((prev) => !prev)}
    >
      {mockMode && (
        <Alert color="yellow" className="mb-4">
          {t('management.dataImporter.alerts.mockMode')}
        </Alert>
      )}

      <Alert color="blue" className="mb-4">
        {t('management.dataImporter.alerts.skipInfo')}
      </Alert>

      {!isAdmin && (
        <Alert color="yellow" className="mb-4">
          {t('management.dataImporter.alerts.authRequired')}
        </Alert>
      )}

      {isAdmin && !mockMode ? (
        <DatabaseImportForm onImportComplete={handleImportComplete} showSkipButton={false} />
      ) : (
        <DatabaseImportForm
          onImportComplete={handleImportComplete}
          showSkipButton={false}
          className="opacity-50 pointer-events-none"
        />
      )}
    </AccordionSection>
  );
};

export default DataImporter;
