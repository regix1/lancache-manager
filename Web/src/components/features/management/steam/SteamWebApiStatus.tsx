import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Alert } from '@components/ui/Alert';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';

import SteamWebApiKeyModal from '@components/modals/setup/SteamWebApiKeyModal';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useSteamWebApiStatus } from '@contexts/useSteamWebApiStatus';
import { usePicsProgress } from '@contexts/usePicsProgress';
import { useNotifications } from '@contexts/notifications';
import ApiService from '@services/api.service';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { ApiError } from '@services/apiError';
import { useAuth } from '@contexts/useAuth';
import { getIntegrationReasonKey } from '../../../../types';

const SteamWebApiStatus: React.FC = () => {
  const { t } = useTranslation();
  const { status, loading, refresh } = useSteamWebApiStatus();
  const { authenticationEnabled, authMode, accountId, sessionId, isLoading } = useAuth();
  const identity = JSON.stringify([authenticationEnabled, authMode, accountId, sessionId]);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const canManage = !isLoading && (authenticationEnabled === false || status?.canManage === true);
  const hasAccess =
    !isLoading &&
    (authenticationEnabled === false ||
      (authMode === 'authenticated' && Boolean(accountId && sessionId)));
  const { updateProgress } = usePicsProgress();
  const { addNotification, updateNotification, scheduleAutoDismiss } = useNotifications();
  const [showConfigModal, setShowConfigModal] = useState(false);
  const [showRemoveModal, setShowRemoveModal] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  useEffect(() => {
    setShowConfigModal(false);
    setShowRemoveModal(false);
    setRemoving(false);
    setRefreshing(false);
  }, [identity]);

  const formattedLastChecked = useFormattedDateTime(status?.lastChecked || null);

  const needsApiKey =
    status?.version === 'V1NoKey' || (status?.version === 'BothFailed' && !status?.hasApiKey);
  const showWarning = !status?.isFullyOperational && !loading;

  const confirmRemoveApiKey = async () => {
    if (identityRef.current !== identity || !canManage || removing) return;
    const caller = identity;
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

      await ApiService.handleResponse(response);
      if (identityRef.current !== caller) return;
      // Whether the API still works without the key is the server's answer to give: it re-tests
      // V2 once the key is gone. Refreshing keeps the panel showing the previous answer until the
      // real one arrives, which is why it does not need a placeholder to fill the gap.
      await refresh();
      if (identityRef.current !== caller) return;

      updateNotification(cardId, {
        status: 'completed',
        message: t('signalr.steamWebApi.keyRemoved'),
        details: { notificationType: 'success' }
      });
      scheduleAutoDismiss(cardId);
    } catch (error: unknown) {
      if (identityRef.current !== caller) return;
      const errorDetail =
        error instanceof ApiError && error.body?.stageKey
          ? t(error.body.stageKey, error.body.context ?? {})
          : t('modals.steamAuth.errors.failedToRemoveApiKey');
      updateNotification(cardId, {
        status: 'failed',
        message: t('signalr.steamWebApi.keyRemoveFailed', { errorDetail }),
        details: { notificationType: 'error' }
      });
      scheduleAutoDismiss(cardId);
    } finally {
      if (identityRef.current === caller) setRemoving(false);
    }
  };

  const handleApiKeySuccess = async () => {
    // The save route stores the key and the server decides what that makes the API, so the status
    // comes from asking it. Every gate elsewhere reads isFullyOperational from this same shared
    // status, so a value invented here would put the panel and those gates on different answers.
    await refresh();
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

  // Anything short of fully operational reads as an error, including the missing-key case: the
  // status line is the only state indicator on this row, so it carries the whole signal.
  const statusTone = loading ? undefined : status?.isFullyOperational ? 'ok' : 'err';
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
        {!canManage && (
          <p className="text-sm text-themed-muted" role="status">
            {status
              ? t(getIntegrationReasonKey(status.ownershipReason))
              : t('errors.integration.statusUnavailable')}
          </p>
        )}
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
              <Button
                variant="filled"
                color="secondary"
                size="sm"
                stableWidth
                onClick={async () => {
                  if (!hasAccess || loading || refreshing) return;
                  const caller = identity;
                  setRefreshing(true);
                  try {
                    await refresh();
                  } finally {
                    if (identityRef.current === caller) setRefreshing(false);
                  }
                }}
                disabled={!hasAccess || loading || refreshing}
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
              <div className="mgmt-row__actions steam-integration__pair">
                <Button
                  variant="filled"
                  color="secondary"
                  size="sm"
                  onClick={() => {
                    if (canManage) setShowConfigModal(true);
                  }}
                  disabled={!canManage || removing}
                >
                  {status?.hasApiKey
                    ? t('management.steamWebApi.updateApiKey')
                    : t('management.steamWebApi.configureApiKey')}
                </Button>
                {status?.hasApiKey && (
                  <Button
                    variant="filled"
                    color="destructive"
                    size="sm"
                    onClick={() => {
                      if (canManage) setShowRemoveModal(true);
                    }}
                    disabled={!canManage || removing || loading}
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
        isOpen={showConfigModal && canManage}
        onClose={() => setShowConfigModal(false)}
        onSuccess={handleApiKeySuccess}
        statusNotifications
      />

      <ConfirmationModal
        opened={showRemoveModal && canManage}
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
