import React from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import type { CorruptionDetectionMethod } from '@/types';

interface CorruptionRemovalWarningProps {
  detectionMethod: CorruptionDetectionMethod;
  /**
   * One sentence naming what this particular modal covers, such as the file count. Everything
   * else is identical across the three removal modals and lives in the summary below.
   */
  scopeNote?: string;
}

/**
 * The shared destructive-removal warning shown in all three corruption removal confirmation
 * modals (Remove, Remove Selected, Remove All).
 *
 * One short paragraph rather than the two stacked alerts and nine bullets it used to be. The
 * bullets spelled out which revalidation checks run and which record tables are touched, which is
 * implementation detail nobody can act on while deciding whether to press the button. What is left
 * is what the reader needs: what disappears, what is spared, and that it is permanent.
 */
const CorruptionRemovalWarning: React.FC<CorruptionRemovalWarningProps> = ({
  detectionMethod,
  scopeNote
}) => {
  const { t } = useTranslation();

  return (
    <Alert color="red">
      <p className="text-sm">
        {t(
          detectionMethod === 'repeated_miss'
            ? 'management.corruption.modal.removalSummaryRepeatedMiss'
            : 'management.corruption.modal.removalSummaryStructural'
        )}
        {scopeNote ? ` ${scopeNote}` : null}
      </p>
    </Alert>
  );
};

export default CorruptionRemovalWarning;
