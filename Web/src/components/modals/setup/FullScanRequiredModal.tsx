import React, { useId } from 'react';
import { AlertTriangle } from 'lucide-react';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { useTranslation } from 'react-i18next';
import { formatCount } from '@utils/formatters';
import '@components/features/management/managementSectionContent.css';

interface FullScanRequiredModalProps {
  opened: boolean;
  changeGap?: number;
  estimatedApps?: number;
  onConfirm?: () => void;
  onCancel: () => void;
  onDownloadFromGitHub: () => void;
  showDownloadOption?: boolean;
  isSteamWebApiAvailable?: boolean;
  isDownloading?: boolean;
}

export const FullScanRequiredModal: React.FC<FullScanRequiredModalProps> = ({
  opened,
  changeGap,
  estimatedApps,
  onConfirm,
  onCancel,
  onDownloadFromGitHub,
  showDownloadOption = true,
  isSteamWebApiAvailable = false,
  isDownloading = false
}) => {
  const { t } = useTranslation();
  const webApiNoteId = useId();

  return (
    <Modal
      opened={opened}
      onClose={onCancel}
      title={
        <div className="flex items-center gap-3">
          <div className="icon-box icon-box--sm full-scan-modal-icon">
            <AlertTriangle className="w-5 h-5" />
          </div>
          <span>{t('app.fullScanRequired.title')}</span>
        </div>
      }
      size="md"
    >
      <div className="full-scan-modal-content">
        {/* `changeGap` is checked rather than short-circuited so a zero gap picks the sentence
            without a number instead of rendering the digit 0. */}
        <p className="full-scan-modal-description">
          {changeGap
            ? t('modals.fullScan.summary', {
                count: changeGap,
                formattedCount: formatCount(changeGap)
              })
            : t('modals.fullScan.summaryNoCount')}
        </p>

        {/* The two ways forward are rows of one list with the same shape, so they read as
            alternatives. Only the recommended one carries the filled button. */}
        <div className="well-surface divided-list">
          {showDownloadOption && (
            <div className="mgmt-row full-scan-modal-choice">
              <div className="mgmt-row__body">
                <div className="mgmt-row__title cluster">
                  <span>{t('modals.fullScan.github.title')}</span>
                  <Badge variant="success">{t('modals.fullScan.github.recommended')}</Badge>
                </div>
                <p className="mgmt-row__meta full-scan-modal-choice-summary">
                  {t('modals.fullScan.github.summary')}
                </p>
              </div>
              <Button
                onClick={onDownloadFromGitHub}
                variant="filled"
                color="run"
                loading={isDownloading}
                aria-busy={isDownloading}
                className="full-scan-modal-choice-action"
              >
                {isDownloading
                  ? t('modals.fullScan.github.downloading')
                  : t('modals.fullScan.github.downloadButton')}
              </Button>
            </div>
          )}

          {/* A full scan enumerates every app through the Steam Web API, which answers either on
              V2 without a key or on V1 with one. When neither route works the choice stays in
              the list with the reason, and its button stays off because the scan would fail. */}
          <div className="mgmt-row full-scan-modal-choice">
            <div className="mgmt-row__body">
              <div className="mgmt-row__title cluster">
                <span>{t('modals.fullScan.full.title')}</span>
              </div>
              <p className="mgmt-row__meta full-scan-modal-choice-summary">
                {estimatedApps
                  ? t('modals.fullScan.full.summary', {
                      count: estimatedApps,
                      formattedCount: formatCount(estimatedApps)
                    })
                  : t('modals.fullScan.full.summaryUnknown')}
              </p>
              {!isSteamWebApiAvailable && (
                <p
                  id={webApiNoteId}
                  className="mgmt-row__meta full-scan-modal-choice-summary text-warning-text"
                >
                  {t('modals.fullScan.full.needsWebApi')}
                </p>
              )}
            </div>
            <Button
              onClick={onConfirm}
              variant="default"
              disabled={!isSteamWebApiAvailable || isDownloading}
              aria-describedby={isSteamWebApiAvailable ? undefined : webApiNoteId}
              className="full-scan-modal-choice-action"
            >
              {t('modals.fullScan.full.startButton')}
            </Button>
          </div>
        </div>

        <div className="confirmation-modal__actions">
          <Button onClick={onCancel} variant="default">
            {t('common.cancel')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};
