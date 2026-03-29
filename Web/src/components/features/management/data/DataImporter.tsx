import React from 'react';
import { useTranslation } from 'react-i18next';
import { Upload } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import { ManagerCardHeader } from '@components/ui/ManagerCard';
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

  const handleImportComplete = (result: ImportResult) => {
    onSuccess?.(
      t('management.dataImporter.messages.importCompleted', {
        imported: result.imported,
        skipped: result.skipped,
        errors: result.errors
      })
    );
    if (onDataRefresh) {
      setTimeout(() => onDataRefresh(), 1000);
    }
  };

  return (
    <Card>
      <ManagerCardHeader
        icon={Upload}
        iconColor="blue"
        title={t('management.dataImporter.title')}
        subtitle={t('management.dataImporter.subtitle')}
      />

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
    </Card>
  );
};

export default DataImporter;
