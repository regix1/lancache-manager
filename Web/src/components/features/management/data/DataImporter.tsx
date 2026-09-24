import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Upload } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { DatabaseImportForm } from './DatabaseImportForm';

interface DataImporterProps {
  isAdmin: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onDataRefresh?: () => void;
}

const DataImporter: React.FC<DataImporterProps> = ({ isAdmin, mockMode, onDataRefresh }) => {
  const { t } = useTranslation();
  const [isExpanded, setIsExpanded] = useState(false);
  useAccordionGroupItem('data-importer', isExpanded, () => setIsExpanded((prev) => !prev));

  // The form's count grid and the import's run card already show the result.
  const handleImportComplete = () => {
    onDataRefresh?.();
  };

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.dataImporter.help.aboutTitle')}>
        {t('management.dataImporter.subtitle')}
      </HelpSection>
    </HelpPopover>
  );

  return (
    <AccordionSection
      title={t('management.dataImporter.title')}
      titleAccessory={helpAccessory}
      icon={Upload}
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
