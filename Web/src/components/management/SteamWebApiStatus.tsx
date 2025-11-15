import React, { useState } from 'react';
import { Globe, AlertCircle, CheckCircle, Key, Loader2, Info, Trash2 } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import SteamWebApiKeyModal from '@components/shared/SteamWebApiKeyModal';
import { useSteamWebApiStatus } from '@contexts/SteamWebApiStatusContext';
import { usePicsProgress } from '@contexts/PicsProgressContext';
import ApiService from '@services/api.service';

interface SteamWebApiStatusProps {
  steamAuthMode?: 'anonymous' | 'authenticated';
}

const SteamWebApiStatus: React.FC<SteamWebApiStatusProps> = ({ steamAuthMode }) => {
  const { status, loading, refresh, updateStatus } = useSteamWebApiStatus();
  const { updateProgress } = usePicsProgress();
  const [showConfigModal, setShowConfigModal] = useState(false);
  const [removing, setRemoving] = useState(false);

  const needsApiKey =
    status?.version === 'V1NoKey' || (status?.version === 'BothFailed' && !status?.hasApiKey);
  const showWarning = !status?.isFullyOperational && !loading;

  const handleRemoveApiKey = async () => {
    if (!confirm('Are you sure you want to remove the Steam Web API key?')) {
      return;
    }

    setRemoving(true);

    try {
      const response = await fetch('/api/steamwebapi/remove-key', {
        method: 'POST',
        headers: ApiService.getHeaders()
      });

      const data = await response.json();

      if (response.ok) {
        // Optimistically update the status immediately - no loading flicker
        updateStatus((prev) => {
          if (!prev) return prev;

          // Update status to reflect removed API key
          const isFullyOperational = prev.isV2Available;

          // Also update PICS progress to reflect Web API availability
          updateProgress((prevProgress) => {
            if (!prevProgress) return prevProgress;
            return {
              ...prevProgress,
              isWebApiAvailable: isFullyOperational
            };
          });

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
      } else {
        alert(data.error || 'Failed to remove API key');
      }
    } catch (error: any) {
      alert(error.message || 'Network error - failed to remove API key');
    } finally {
      setRemoving(false);
    }
  };

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
        <div className="flex items-center space-x-2 mb-4">
          <Globe className="w-5 h-5 text-themed-primary" />
          <h3 className="text-lg font-semibold text-themed-primary">Steam Web API Status</h3>
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

        {/* Info Box */}
        <div
          className="mb-4 p-3 rounded-lg border"
          style={{
            backgroundColor: 'var(--theme-info-bg)',
            borderColor: 'var(--theme-info)'
          }}
        >
          <div className="flex items-start gap-3">
            <Info className="w-5 h-5 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-info)' }} />
            <div className="flex-1">
              <p className="text-xs" style={{ color: 'var(--theme-info-text)', opacity: 0.9 }}>
                <strong>What is this?</strong> Steam provides two Web API versions. V2 requires no
                API key but may become unavailable. V1 requires an API key but provides a reliable
                fallback.
                <br />
                <br />
                <strong>API key is optional</strong> and only needed if V2 becomes unavailable.
                {steamAuthMode === 'anonymous' && (
                  <>
                    {' '}
                    Anonymous users can alternatively use the GitHub download option for depot
                    mappings.
                  </>
                )}
              </p>
            </div>
          </div>
        </div>

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
                variant="outline"
                color="red"
                leftSection={<Trash2 className="w-4 h-4" />}
                onClick={handleRemoveApiKey}
                disabled={removing || loading}
                loading={removing}
              >
                Remove
              </Button>
            )}
          </div>
        )}

        {/* Last Checked */}
        {!loading && status && (
          <p className="text-xs text-themed-muted mt-3 text-center">
            Last checked: {new Date(status.lastChecked).toLocaleString()}
          </p>
        )}
      </Card>

      {/* Configuration Modal */}
      <SteamWebApiKeyModal
        isOpen={showConfigModal}
        onClose={() => setShowConfigModal(false)}
        onSuccess={() => {
          // Optimistically update the status immediately - no loading flicker
          updateStatus((prev) => {
            if (!prev) return prev;

            // Also update PICS progress to reflect Web API availability
            updateProgress((prevProgress) => {
              if (!prevProgress) return prevProgress;
              return {
                ...prevProgress,
                isWebApiAvailable: true
              };
            });

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
        }}
      />
    </>
  );
};

export default SteamWebApiStatus;
