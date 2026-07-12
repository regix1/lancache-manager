import React from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import type { CorruptionDetectionMethod } from '@/types';

interface CorruptionRemovalWarningProps {
  detectionMethod: CorruptionDetectionMethod;
  /**
   * Extra <li> cautions appended to the yellow "important" list. The red
   * "will delete" block and shared revalidation cautions are identical across
   * every corruption removal modal;
   * only these trailing cautions differ per modal.
   */
  extraCautions?: React.ReactNode;
}

/**
 * The shared destructive-removal warning shown in all three corruption removal
 * confirmation modals (Remove, Remove Selected, Remove All). Extracted so the
 * identical red "will delete" alert and shared cautions live in one place.
 */
const CorruptionRemovalWarning: React.FC<CorruptionRemovalWarningProps> = ({
  detectionMethod,
  extraCautions
}) => {
  const { t } = useTranslation();
  const removesRepeatedMissEvidence = detectionMethod === 'repeated_miss';

  return (
    <>
      <Alert color="red">
        <div>
          <p className="text-sm font-medium mb-2">{t('management.corruption.modal.willDelete')}</p>
          <ul className="list-disc list-inside text-sm space-y-1 ml-2">
            <li>
              <strong>{t('management.corruption.modal.cacheFilesLabel')}</strong>{' '}
              {t('management.corruption.modal.cacheFilesDesc')}
            </li>
            {removesRepeatedMissEvidence && (
              <>
                <li>
                  <strong>{t('management.corruption.modal.logEntriesLabel')}</strong>{' '}
                  {t('management.corruption.modal.logEntriesDesc')}
                </li>
                <li>
                  <strong>{t('management.corruption.modal.databaseRecordsLabel')}</strong>{' '}
                  {t('management.corruption.modal.databaseRecordsDesc')}
                </li>
              </>
            )}
            <li>
              <strong>{t('management.corruption.modal.savedScanRecordsLabel')}</strong>{' '}
              {t('management.corruption.modal.savedScanRecordsDesc')}
            </li>
          </ul>
        </div>
      </Alert>

      <Alert color="yellow">
        <div>
          <p className="text-sm font-medium mb-2">{t('management.cache.alerts.important')}</p>
          <ul className="list-disc list-inside text-sm space-y-1 ml-2">
            <li>{t('management.corruption.modal.cannotBeUndone')}</li>
            <li>{t('management.corruption.modal.revalidationSkip')}</li>
            <li>{t('management.corruption.modal.mayTakeSeveralMinutes')}</li>
            {extraCautions}
          </ul>
        </div>
      </Alert>
    </>
  );
};

export default CorruptionRemovalWarning;
