import React, { useState, useEffect, useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Settings2 } from 'lucide-react';
import '../managementSectionContent.css';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useSectionExpanded } from '@hooks/useSectionExpanded';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { AccordionSection } from '@components/ui/AccordionSection';
import { SectionErrorChip } from '@components/ui/SectionHeaderActions';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatBytes } from '@utils/formatters';
import { getErrorMessage } from '@utils/error';

// The manual sizes offered: the powers of two from 1 MiB to 64 MiB that the server also tries.
const MANUAL_SLICE_SIZES = [1, 2, 4, 8, 16, 32, 64].map(
  (mebibytes: number): number => mebibytes * 1024 * 1024
);

interface CacheSettingsProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError: (message: string, error?: unknown) => void;
}

const CacheSettings: React.FC<CacheSettingsProps> = ({ authMode, mockMode, onError }) => {
  const { t } = useTranslation();
  const signalR = useSignalR();
  // Null is Auto: the cache files' own slice size is read on every scan. Undefined is not loaded
  // yet, so the control never shows or saves a value the server did not send.
  const [sliceSize, setSliceSize] = useState<number | null | undefined>(undefined);
  const [sliceSizeLoadError, setSliceSizeLoadError] = useState<string | null>(null);
  const sliceSizeChangeInProgressRef = useRef(false);
  // The mount, reconnect, change-event and Retry reads can overlap; only the newest one writes.
  const sliceSizeRequestRef = useRef(0);
  const [sectionExpanded, setSectionExpanded] = useSectionExpanded(
    'management-cache-settings-expanded',
    true
  );
  useAccordionGroupItem('storage-cache-settings', sectionExpanded, () =>
    setSectionExpanded((prev) => !prev)
  );

  const loadSliceSize = useCallback(() => {
    const request = ++sliceSizeRequestRef.current;
    ApiService.getCacheSliceSize()
      .then((bytes: number | null) => {
        if (request !== sliceSizeRequestRef.current) return;
        setSliceSize(bytes);
        setSliceSizeLoadError(null);
      })
      .catch((err: unknown) => {
        console.error('Failed to load cache slice size:', err);
        if (request !== sliceSizeRequestRef.current) return;
        setSliceSizeLoadError(getErrorMessage(err));
      });
  }, []);
  useEffect(() => {
    loadSliceSize();
  }, [loadSliceSize]);
  useReconnectRefetch(signalR.isConnected, loadSliceSize);
  // A save from another tab, another admin or the PATCH request would otherwise leave this page on
  // the size it loaded.
  useEffect(() => {
    signalR.on('CacheSliceSizeChanged', loadSliceSize);
    return () => {
      signalR.off('CacheSliceSizeChanged', loadSliceSize);
    };
  }, [signalR, loadSliceSize]);

  const handleSliceSizeChange = async (value: string) => {
    const bytes = value === 'auto' ? null : Number(value);
    if (bytes === sliceSize || sliceSizeChangeInProgressRef.current) {
      return;
    }
    sliceSizeChangeInProgressRef.current = true;

    try {
      await ApiService.setCacheSliceSize(bytes);
      setSliceSize(bytes);
    } catch (err: unknown) {
      console.error('Failed to update cache slice size:', err);
      onError(t('management.sections.displayPreferences.saveFailed'), err);
    } finally {
      sliceSizeChangeInProgressRef.current = false;
    }
  };

  const sliceSizeOptions: DropdownOption[] = [
    { value: 'auto', label: t('management.cacheSliceSize.auto') },
    // The API stores any whole number of MiB, so a size outside the list gets its own option.
    ...(sliceSize == null || MANUAL_SLICE_SIZES.includes(sliceSize)
      ? MANUAL_SLICE_SIZES
      : [...MANUAL_SLICE_SIZES, sliceSize].sort((a: number, b: number): number => a - b)
    ).map(
      (bytes: number): DropdownOption => ({ value: String(bytes), label: formatBytes(bytes, 0) })
    )
  ];

  return (
    <AccordionSection
      title={t('management.cacheSliceSize.cardTitle')}
      icon={Settings2}
      isExpanded={sectionExpanded}
      onToggle={() => setSectionExpanded((prev) => !prev)}
      badge={sliceSizeLoadError !== null && !sectionExpanded && <SectionErrorChip />}
    >
      <div className="space-y-3">
        <div>
          <p className="mgmt-subhead caps-label">{t('management.cacheSliceSize.label')}</p>
          <p className="text-xs text-themed-muted mt-1">
            {t('management.cacheSliceSize.description')}
          </p>
        </div>
        {sliceSizeLoadError ? (
          <ErrorBlock
            title={t('management.cacheSliceSize.loadFailed')}
            message={sliceSizeLoadError}
            retryLabel={t('common.retry')}
            onRetry={loadSliceSize}
          />
        ) : sliceSize === undefined ? (
          // Same height as the dropdown that replaces it, so the cards below do not move.
          <div className="min-h-10 flex items-center">
            <LoadingSpinner inline size="xs" />
          </div>
        ) : (
          <div className="w-full sm:w-60">
            <EnhancedDropdown
              variant="button"
              options={sliceSizeOptions}
              value={sliceSize === null ? 'auto' : String(sliceSize)}
              onChange={handleSliceSizeChange}
              prefix={sliceSize === null ? undefined : t('management.cacheSliceSize.manual')}
              disabled={mockMode || authMode !== 'authenticated'}
              dropdownTitle={t('management.cacheSliceSize.label')}
              triggerAriaLabel={t('management.cacheSliceSize.label')}
              compactMode={true}
            />
          </div>
        )}
      </div>
    </AccordionSection>
  );
};

export default CacheSettings;
