import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Globe, AlertCircle, CheckCircle, XCircle, Key, Loader2, Trash2, AlertTriangle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import HighlightGlow from '@components/ui/HighlightGlow';
import SteamWebApiKeyModal from '@components/modals/setup/SteamWebApiKeyModal';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';
import { usePicsProgress } from '@contexts/PicsProgressContext';
import ApiService from '@services/api.service';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';

interface SteamWebApiStatusProps {
  steamAuthMode?: 'anonymous' | 'authenticated';
  highlight?: boolean;
}

const SteamWebApiStatus: React.FC<SteamWebApiStatusProps> = ({ steamAuthMode: _steamAuthMode, highlight }) => {
  const { t } = useTranslation();
  const { status, loading, refresh, updateStatus } = useSteamWebApiStatus();
  const { updateProgress } = usePicsProgress();
  const [showConfigModal, setShowConfigModal] = useState(false);
  const [showRemoveModal, setShowRemoveModal] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  // Format last checked time with timezone awareness
  const formattedLastChecked = useFormattedDateTime(status?.lastChecked || null);

  const needsApiKey =
    status?.version === 'V1NoKey' || (status?.version === 'BothFailed' && !status?.hasApiKey);
  const showWarning = !status?.isFullyOperational && !loading;

  const confirmRemoveApiKey = async () => {
    setRemoving(true);
    setShowRemoveModal(false);

    try {
      const response = await fetch('/api/steam-api-keys/current', ApiService.getFetchOptions({
        method: 'DELETE'
      }));

      const data = await response.json();

      if (response.ok) {
        // Optimistically update the status immediately - no loading flicker
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

        // Update PICS progress in useEffect, not during render
      } else {
        alert(data.error || t('modals.steamAuth.errors.failedToRemoveApiKey'));
      }
    } catch (error: unknown) {
      alert((error instanceof Error ? error.message : String(error)) || t('modals.steamAuth.errors.networkError'));
    } finally {
      setRemoving(false);
    }
  };

  const handleApiKeySuccess = () => {
    // Optimistically update the status immediately - no loading flicker
    updateStatus((prev) => {
      if (!prev) return prev;

      // Update status to reflect added/updated API key
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

    // Update PICS progress will happen in useEffect
  };

  // Sync Steam Web API status to PICS progress context using useEffect
  // This prevents setState during render
  useEffect(() => {
    if (status) {
      updateProgress((prevProgress) => {
        if (!prevProgress) return prevProgress;

        const newIsWebApiAvailable = status.isFullyOperational;

        // Only update if the value changed to avoid unnecessary re-renders
        if (prevProgress.isWebApiAvailable === newIsWebApiAvailable) {
          return prevProgress;
        }

        return {
          ...prevProgress,
          isWebApiAvailable: newIsWebApiAvailable
        };
      });
    }
  }, [status?.isFullyOperational, updateProgress]);

  const getStatusIcon = () => {
    if (loading) {
      return <Loader2 className="w-5 h-5 animate-spin text-themed-muted" />;
    }

    if (status?.isFullyOperational) {
      return <CheckCircle className="w-5 h-5 icon-success" />;
    }

    if (needsApiKey) {
      return <AlertCircle className="w-5 h-5 icon-warning" />;
    }

    return <AlertCircle className="w-5 h-5 icon-error" />;
  };

  const getStatusColor = () => {
    if (loading) return 'var(--theme-muted)';
    if (status?.isFullyOperational) return 'var(--theme-success)';
    if (needsApiKey) return 'var(--theme-warning)';
    return 'var(--theme-error)';
  };

  const getVersionBadge = (version: string, available: boolean, needsKey?: boolean) => {
    const Icon = available ? CheckCircle : (needsKey ? AlertTriangle : XCircle);
    const colorClass = available
      ? 'bg-themed-success text-themed-success border-[var(--theme-success)]'
      : needsKey
        ? 'bg-themed-warning text-themed-warning border-[var(--theme-warning)]'
        : 'bg-themed-error text-themed-error border-[var(--theme-error)]';

    return (
      <span className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium border ${colorClass}`}>
        <Icon className="w-3.5 h-3.5" />
        {version}
      </span>
    );
  };

  return (
    <>
      <HighlightGlow enabled={highlight}>
        <Card>
          <div className="flex items-center gap-3 mb-4">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-cyan">
            <Globe className="w-5 h-5 icon-cyan" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">{t('management.steamWebApi.title')}</h3>
          <HelpPopover position="left" width={320}>
            <HelpSection title={t('management.steamWebApi.help.apiVersions.title')} variant="subtle">
              <div className="divide-y divide-[var(--theme-text-muted)]">
                <div className="py-1.5 first:pt-0 last:pb-0">
                  <div className="font-medium text-themed-primary">{t('management.steamWebApi.help.apiVersions.v2.term')}</div>
                  <div className="mt-0.5">{t('management.steamWebApi.help.apiVersions.v2.description')}</div>
                </div>
                <div className="py-1.5 first:pt-0 last:pb-0">
                  <div className="font-medium text-themed-primary">{t('management.steamWebApi.help.apiVersions.v1.term')}</div>
                  <div className="mt-0.5">{t('management.steamWebApi.help.apiVersions.v1.description')}</div>
                </div>
              </div>
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
              </a>
              {' '}{t('management.steamWebApi.help.getApiKey.after')}
            </HelpNote>
          </HelpPopover>
        </div>

        {/* Status Overview */}
        <div className="p-4 rounded-lg mb-4 bg-themed-tertiary">
          {/* Status message - full width */}
          <div className="flex items-start gap-2.5 mb-3">
            <div className="mt-0.5 flex-shrink-0">{getStatusIcon()}</div>
            <p
              className="text-sm font-medium leading-relaxed"
              style={{ color: getStatusColor() }}
            >
              {loading ? t('management.steamWebApi.checkingStatus') : status?.message || t('management.steamWebApi.unknownStatus')}
            </p>
          </div>

          {/* Version badges and refresh button */}
          <div className="flex items-center justify-between gap-3">
            {!loading && status ? (
              <div className="flex flex-wrap gap-2">
                {getVersionBadge('V2', status.isV2Available)}
                {getVersionBadge(
                  status.hasApiKey ? t('management.steamWebApi.v1WithKey') : t('management.steamWebApi.v1NoKey'),
                  status.isV1Available,
                  !status.isV1Available && !status.hasApiKey
                )}
              </div>
            ) : (
              <div />
            )}
            <Button
              variant="outline"
              size="sm"
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
              className="flex-shrink-0"
            >
              {t('common.refresh')}
            </Button>
          </div>
        </div>

        {/* Warning Banner - Only show for critical errors (both APIs down with key configured) */}
        {showWarning && status?.version === 'BothFailed' && status?.hasApiKey && (
          <div className="mb-4 p-3 rounded-lg border bg-themed-error border-error">
            <div className="flex items-start gap-3">
              <AlertCircle className="w-5 h-5 flex-shrink-0 mt-0.5 icon-error" />
              <div className="flex-1">
                <p className="font-medium text-sm mb-1 text-themed-error">
                  {t('management.steamWebApi.bothUnavailable.title')}
                </p>
                <p className="text-xs text-themed-error opacity-90">
                  {t('management.steamWebApi.bothUnavailable.description')}
                </p>
              </div>
            </div>
          </div>
        )}


        {/* Configure/Remove Buttons */}
        {(needsApiKey || status?.hasApiKey) && (
          <div className="flex gap-2">
            <Button
              variant="filled"
              color="blue"
              leftSection={<Key className="w-4 h-4" />}
              onClick={() => setShowConfigModal(true)}
              className="flex-1"
              disabled={removing}
            >
              {status?.hasApiKey ? t('management.steamWebApi.updateApiKey') : t('management.steamWebApi.configureApiKey')}
            </Button>
            {status?.hasApiKey && (
              <Button
                variant="filled"
                color="red"
                leftSection={<Trash2 className="w-4 h-4" />}
                onClick={() => setShowRemoveModal(true)}
                disabled={removing || loading}
              >
                {t('management.steamWebApi.remove')}
              </Button>
            )}
          </div>
        )}

          {/* Last Checked */}
          {!loading && status && (
            <p className="text-xs text-themed-muted mt-3 text-center">
              {t('management.steamWebApi.lastChecked')}: {formattedLastChecked}
            </p>
          )}
        </Card>
      </HighlightGlow>

      {/* Configuration Modal */}
      <SteamWebApiKeyModal
        isOpen={showConfigModal}
        onClose={() => setShowConfigModal(false)}
        onSuccess={handleApiKeySuccess}
      />

      {/* Remove Confirmation Modal */}
      <Modal
        opened={showRemoveModal}
        onClose={() => {
          if (!removing) {
            setShowRemoveModal(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.steamWebApi.removeModal.title')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.steamWebApi.removeModal.message')}
          </p>

          <Alert color="yellow">
            <p className="text-sm">{t('management.steamWebApi.removeModal.warning')}</p>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setShowRemoveModal(false)} disabled={removing}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<Trash2 className="w-4 h-4" />}
              onClick={confirmRemoveApiKey}
              loading={removing}
            >
              {t('management.steamWebApi.removeModal.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default SteamWebApiStatus;
