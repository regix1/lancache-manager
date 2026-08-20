import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Alert } from '@components/ui/Alert';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import Badge from '@components/ui/Badge';

import SteamWebApiKeyModal from '@components/modals/setup/SteamWebApiKeyModal';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useSteamWebApiStatus } from '@contexts/useSteamWebApiStatus';
import { usePicsProgress } from '@contexts/usePicsProgress';
import { useNotifications } from '@contexts/notifications';
import ApiService from '@services/api.service';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { getErrorMessage } from '@utils/error';

const SteamWebApiStatus: React.FC = () => {
  const { t } = useTranslation();
  const { status, loading, refresh, updateStatus } = useSteamWebApiStatus();
  const { updateProgress } = usePicsProgress();
  const { addNotification, updateNotification, scheduleAutoDismiss } = useNotifications();
  const [showConfigModal, setShowConfigModal] = useState(false);
  const [showRemoveModal, setShowRemoveModal] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  const formattedLastChecked = useFormattedDateTime(status?.lastChecked || null);

  const needsApiKey =
    status?.version === 'V1NoKey' || (status?.version === 'BothFailed' && !status?.hasApiKey);
  const showWarning = !status?.isFullyOperational && !loading;

  const confirmRemoveApiKey = async () => {
    setRemoving(true);
    setShowRemoveModal(false);

    const cardId = addNotification({
      type: 'generic',
      status: 'running',
      message: t('signalr.steamWebApi.removing'),
      details: { notificationType: 'info' }
    });

    try {
      const response = await fetch(
        '/api/steam-api-keys/current',
        ApiService.getFetchOptions({
          method: 'DELETE'
        })
      );

      const data = await response.json();

      if (response.ok) {
        updateStatus((prev) => {
          if (!prev) return prev;

          const isFullyOperational = prev.isV2Available;

          return {
            ...prev,
            hasApiKey: false,
            version: prev.isV2Available ? 'V2Only' : 'V1NoKey',
            isV1Available: false,
            isFullyOperational,
            message: prev.isV2Available
              ? 'Steam Web API V2 operational'
              : 'Steam Web API V2 unavailable - V1 requires API key (not configured)',
            lastChecked: new Date().toISOString()
          };
        });

        updateNotification(cardId, {
          status: 'completed',
          message: t('signalr.steamWebApi.keyRemoved'),
          details: { notificationType: 'success' }
        });
        scheduleAutoDismiss(cardId);
      } else {
        const errorDetail = data.error || t('modals.steamAuth.errors.failedToRemoveApiKey');
        updateNotification(cardId, {
          status: 'failed',
          message: t('signalr.steamWebApi.keyRemoveFailed', { errorDetail }),
          details: { notificationType: 'error' }
        });
        scheduleAutoDismiss(cardId);
      }
    } catch (error: unknown) {
      const errorDetail = getErrorMessage(error) || t('modals.steamAuth.errors.networkError');
      updateNotification(cardId, {
        status: 'failed',
        message: t('signalr.steamWebApi.keyRemoveFailed', { errorDetail }),
        details: { notificationType: 'error' }
      });
      scheduleAutoDismiss(cardId);
    } finally {
      setRemoving(false);
    }
  };

  const handleApiKeySuccess = () => {
    updateStatus((prev) => {
      if (!prev) return prev;

      return {
        ...prev,
        hasApiKey: true,
        isV1Available: true,
        isFullyOperational: true,
        version: 'V1WithKey',
        message: 'Steam Web API V1 operational with API key',
        lastChecked: new Date().toISOString()
      };
    });
  };

  useEffect(() => {
    if (status) {
      updateProgress((prevProgress) => {
        if (!prevProgress) return prevProgress;

        const newIsWebApiAvailable = status.isFullyOperational;

        if (prevProgress.isWebApiAvailable === newIsWebApiAvailable) {
          return prevProgress;
        }

        return {
          ...prevProgress,
          isWebApiAvailable: newIsWebApiAvailable
        };
      });
    }
  }, [status?.isFullyOperational, status, updateProgress]);

  const statusTone = loading
    ? undefined
    : status?.isFullyOperational
      ? 'ok'
      : needsApiKey
        ? 'warn'
        : 'err';
  const stateLabel = loading
    ? t('management.steamWebApi.checkingStatus')
    : !status
      ? t('management.steamWebApi.unknownStatus')
      : status.isFullyOperational
        ? t('management.steamWebApi.state.operational')
        : needsApiKey
          ? t('management.steamWebApi.state.needsKey')
          : t('management.steamWebApi.state.down');
  const statusTitleClass = [
    'mgmt-row__title',
    'steam-integration__status',
    statusTone ? `steam-integration__status--${statusTone}` : ''
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <>
      <div className="steam-integration">
        <div className="steam-integration__subhead">
          <h4 className="mgmt-subhead caps-label">{t('management.steamWebApi.sectionTitle')}</h4>
          <HelpPopover position="left" width={320}>
            <HelpSection
              title={t('management.steamWebApi.help.apiVersions.title')}
              variant="subtle"
            >
              <HelpDefinition
                items={[
                  {
                    term: t('management.steamWebApi.help.apiVersions.v2.term'),
                    description: t('management.steamWebApi.help.apiVersions.v2.description')
                  },
                  {
                    term: t('management.steamWebApi.help.apiVersions.v1.term'),
                    description: t('management.steamWebApi.help.apiVersions.v1.description')
                  }
                ]}
              />
            </HelpSection>

            <HelpSection title={t('management.steamWebApi.help.apiKey.title')} variant="subtle">
              {t('management.steamWebApi.help.apiKey.description')}
            </HelpSection>

            <HelpNote type="info">
              {t('management.steamWebApi.help.getApiKey.before')}{' '}
              <a
                href="https://steamcommunity.com/dev/apikey"
                target="_blank"
                rel="noopener noreferrer"
                className="font-medium underline hover:no-underline text-themed-primary"
              >
                steamcommunity.com/dev/apikey
              </a>{' '}
              {t('management.steamWebApi.help.getApiKey.after')}
            </HelpNote>
          </HelpPopover>
        </div>

        {showWarning && status?.version === 'BothFailed' && status?.hasApiKey && (
          <Alert color="red" title={t('management.steamWebApi.bothUnavailable.title')}>
            {t('management.steamWebApi.bothUnavailable.description')}
          </Alert>
        )}

        <div className="mgmt-list">
          <div className="mgmt-row">
            <div className="mgmt-row__body">
              <p className={statusTitleClass}>
                {loading && <LoadingSpinner inline size="xs" />}
                {stateLabel}
              </p>
              {!loading && status && (
                <p className="mgmt-row__meta">
                  {t('management.steamWebApi.lastChecked')}: {formattedLastChecked}
                </p>
              )}
            </div>
            <div className="mgmt-row__actions">
              {!loading && status && (
                <>
                  <Badge variant={status.isV2Available ? 'success' : 'error'}>V2</Badge>
                  <Badge
                    variant={
                      status.isV1Available ? 'success' : !status.hasApiKey ? 'warning' : 'error'
                    }
                  >
                    {status.hasApiKey
                      ? t('management.steamWebApi.v1WithKey')
                      : t('management.steamWebApi.v1NoKey')}
                  </Badge>
                </>
              )}
              <Button
                variant="filled"
                color="gray"
                size="sm"
                stableWidth
                onClick={async () => {
                  setRefreshing(true);
                  try {
                    await refresh();
                  } finally {
                    setRefreshing(false);
                  }
                }}
                disabled={loading || refreshing}
                loading={refreshing}
              >
                {t('common.refresh')}
              </Button>
            </div>
          </div>

          {(needsApiKey || status?.hasApiKey) && (
            <div className="mgmt-row">
              <div className="mgmt-row__body">
                <p className="mgmt-row__title">{t('management.steamWebApi.keyRow')}</p>
                <p className="mgmt-row__meta">
                  {status?.hasApiKey
                    ? t('management.steamWebApi.keyConfigured')
                    : t('management.steamWebApi.keyMissing')}
                </p>
              </div>
              <div className="mgmt-row__actions">
                <Button
                  variant="filled"
                  color="blue"
                  size="sm"
                  onClick={() => setShowConfigModal(true)}
                  disabled={removing}
                >
                  {status?.hasApiKey
                    ? t('management.steamWebApi.updateApiKey')
                    : t('management.steamWebApi.configureApiKey')}
                </Button>
                {status?.hasApiKey && (
                  <Button
                    variant="filled"
                    color="red"
                    size="sm"
                    onClick={() => setShowRemoveModal(true)}
                    disabled={removing || loading}
                  >
                    {t('management.steamWebApi.remove')}
                  </Button>
                )}
              </div>
            </div>
          )}
        </div>
      </div>

      <SteamWebApiKeyModal
        isOpen={showConfigModal}
        onClose={() => setShowConfigModal(false)}
        onSuccess={handleApiKeySuccess}
        statusNotifications
      />

      <ConfirmationModal
        opened={showRemoveModal}
        onClose={() => setShowRemoveModal(false)}
        onConfirm={confirmRemoveApiKey}
        title={t('management.steamWebApi.removeModal.title')}
        confirmLabel={t('management.steamWebApi.removeModal.confirm')}
        loading={removing}
      >
        <p className="text-themed-secondary">{t('management.steamWebApi.removeModal.message')}</p>

        <Alert color="yellow">
          <p className="text-sm">{t('management.steamWebApi.removeModal.warning')}</p>
        </Alert>
      </ConfirmationModal>
    </>
  );
};

export default SteamWebApiStatus;
