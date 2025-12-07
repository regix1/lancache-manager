import React, { useState, useEffect } from 'react';
import { Globe, AlertCircle, CheckCircle, Key, Loader2, Trash2, AlertTriangle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { HelpPopover, HelpSection, HelpNote, HelpKeyword, HelpDefinition } from '@components/ui/HelpPopover';
import SteamWebApiKeyModal from '@components/modals/setup/SteamWebApiKeyModal';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';
import { usePicsProgress } from '@contexts/PicsProgressContext';
import ApiService from '@services/api.service';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';

interface SteamWebApiStatusProps {
  steamAuthMode?: 'anonymous' | 'authenticated';
}

const SteamWebApiStatus: React.FC<SteamWebApiStatusProps> = ({ steamAuthMode: _steamAuthMode }) => {
  const { status, loading, refresh, updateStatus } = useSteamWebApiStatus();
  const { updateProgress } = usePicsProgress();
  const [showConfigModal, setShowConfigModal] = useState(false);
  const [showRemoveModal, setShowRemoveModal] = useState(false);
  const [removing, setRemoving] = useState(false);

  // Format last checked time with timezone awareness
  const formattedLastChecked = useFormattedDateTime(status?.lastChecked || null);

  const needsApiKey =
    status?.version === 'V1NoKey' || (status?.version === 'BothFailed' && !status?.hasApiKey);
  const showWarning = !status?.isFullyOperational && !loading;

  const confirmRemoveApiKey = async () => {
    setRemoving(true);
    setShowRemoveModal(false);

    try {
      const response = await fetch('/api/steam-api-keys/current', {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

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
        alert(data.error || 'Failed to remove API key');
      }
    } catch (error: unknown) {
      alert((error instanceof Error ? error.message : String(error)) || 'Network error - failed to remove API key');
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
      return <CheckCircle className="w-5 h-5" style={{ color: 'var(--theme-success)' }} />;
    }

    if (needsApiKey) {
      return <AlertCircle className="w-5 h-5" style={{ color: 'var(--theme-warning)' }} />;
    }

    return <AlertCircle className="w-5 h-5" style={{ color: 'var(--theme-error)' }} />;
  };

  const getStatusColor = () => {
    if (loading) return 'var(--theme-muted)';
    if (status?.isFullyOperational) return 'var(--theme-success)';
    if (needsApiKey) return 'var(--theme-warning)';
    return 'var(--theme-error)';
  };

  const getVersionBadge = (version: string, available: boolean) => {
    const bgColor = available ? 'var(--theme-success-bg)' : 'var(--theme-muted-bg)';
    const textColor = available ? 'var(--theme-success-text)' : 'var(--theme-muted)';

    return (
      <span
        className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium"
        style={{ backgroundColor: bgColor, color: textColor }}
      >
        {version} {available ? '✓' : '✗'}
      </span>
    );
  };

  return (
    <>
      <Card>
        <div className="flex items-center gap-3 mb-4">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-cyan">
            <Globe className="w-5 h-5 icon-cyan" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary">Steam Web API Status</h3>
          <HelpPopover position="left" width={340}>
            <HelpSection title="API Versions">
              <div className="space-y-1.5">
                <HelpDefinition term="V2" termColor="green">
                  No API key required — may become unavailable during high load
                </HelpDefinition>
                <HelpDefinition term="V1" termColor="blue">
                  Requires API key — provides reliable fallback when V2 is down
                </HelpDefinition>
              </div>
            </HelpSection>

            <HelpSection title="API Key" variant="subtle">
              <HelpKeyword color="cyan">Optional</HelpKeyword> — only needed if V2 becomes unavailable.
              Anonymous users can use the <HelpKeyword color="purple">GitHub download</HelpKeyword> option
              as an alternative.
            </HelpSection>

            <HelpNote type="info">
              Get your free API key at{' '}
              <a
                href="https://steamcommunity.com/dev/apikey"
                target="_blank"
                rel="noopener noreferrer"
                className="font-medium underline hover:no-underline"
                style={{ color: 'var(--theme-info-text)' }}
              >
                steamcommunity.com/dev/apikey
              </a>
            </HelpNote>

            <HelpNote type="tip">
              After requesting, open the <HelpKeyword color="purple">Steam Mobile App</HelpKeyword> →
              hamburger menu (bottom right) → Confirmations → approve the request.
            </HelpNote>
          </HelpPopover>
        </div>

        {/* Status Overview */}
        <div className="mb-4">
          <div className="flex items-center justify-between mb-3">
            <div className="flex items-center gap-2">
              {getStatusIcon()}
              <span className="text-sm font-medium" style={{ color: getStatusColor() }}>
                {loading ? 'Checking status...' : status?.message || 'Unknown status'}
              </span>
            </div>
            <Button variant="subtle" size="sm" onClick={() => refresh()} disabled={loading}>
              Refresh
            </Button>
          </div>

          {/* Version Status */}
          {!loading && status && (
            <div className="flex flex-wrap gap-2 mb-3">
              {getVersionBadge('V2', status.isV2Available)}
              {getVersionBadge(
                status.hasApiKey ? 'V1 (with key)' : 'V1 (no key)',
                status.isV1Available
              )}
            </div>
          )}
        </div>

        {/* Warning/Info Banners */}
        {showWarning && needsApiKey && (
          <div
            className="mb-4 p-3 rounded-lg border"
            style={{
              backgroundColor: 'var(--theme-warning-bg)',
              borderColor: 'var(--theme-warning)'
            }}
          >
            <div className="flex items-start gap-3">
              <AlertCircle
                className="w-5 h-5 flex-shrink-0 mt-0.5"
                style={{ color: 'var(--theme-warning)' }}
              />
              <div className="flex-1">
                <p className="font-medium text-sm mb-1" style={{ color: 'var(--theme-warning-text)' }}>
                  Steam Web API V2 Unavailable
                </p>
                <p className="text-xs" style={{ color: 'var(--theme-warning-text)', opacity: 0.9 }}>
                  The Steam Web API V2 is currently unavailable. To enable full functionality,
                  please configure a Steam Web API key for V1 fallback.
                </p>
              </div>
            </div>
          </div>
        )}

        {showWarning && status?.version === 'BothFailed' && status?.hasApiKey && (
          <div
            className="mb-4 p-3 rounded-lg border"
            style={{
              backgroundColor: 'var(--theme-error-bg)',
              borderColor: 'var(--theme-error)'
            }}
          >
            <div className="flex items-start gap-3">
              <AlertCircle
                className="w-5 h-5 flex-shrink-0 mt-0.5"
                style={{ color: 'var(--theme-error)' }}
              />
              <div className="flex-1">
                <p className="font-medium text-sm mb-1" style={{ color: 'var(--theme-error-text)' }}>
                  Both V2 and V1 Unavailable
                </p>
                <p className="text-xs" style={{ color: 'var(--theme-error-text)', opacity: 0.9 }}>
                  Both Steam Web API V2 and V1 are currently unavailable. This may be a temporary
                  Steam service issue. Please try again later.
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
              {status?.hasApiKey ? 'Update API Key' : 'Configure API Key'}
            </Button>
            {status?.hasApiKey && (
              <Button
                variant="filled"
                color="red"
                leftSection={<Trash2 className="w-4 h-4" />}
                onClick={() => setShowRemoveModal(true)}
                disabled={removing || loading}
              >
                Remove
              </Button>
            )}
          </div>
        )}

        {/* Last Checked */}
        {!loading && status && (
          <p className="text-xs text-themed-muted mt-3 text-center">
            Last checked: {formattedLastChecked}
          </p>
        )}
      </Card>

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
            <span>Remove Steam Web API Key</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to remove the Steam Web API key? If V2 becomes unavailable,
            you will need to reconfigure the key to use V1 fallback.
          </p>

          <Alert color="yellow">
            <p className="text-sm">This action will remove the stored API key from the server.</p>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setShowRemoveModal(false)} disabled={removing}>
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<Trash2 className="w-4 h-4" />}
              onClick={confirmRemoveApiKey}
              loading={removing}
            >
              Remove API Key
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default SteamWebApiStatus;
