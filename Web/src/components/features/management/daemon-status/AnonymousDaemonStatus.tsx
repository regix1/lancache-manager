import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import DaemonStatusCard from './DaemonStatusCard';
import type { AnonymousDaemonCopy, AnonymousDaemonService } from './daemonStatus.types';
import type { DaemonStatusDto } from '../../../../types';

/** Applied after a failed read so the card reports a definite offline state rather than a blank one. */
const OFFLINE_STATUS: DaemonStatusDto = {
  dockerAvailable: false,
  activeSessions: 0,
  maxSessionsPerUser: 1,
  sessionTimeoutMinutes: 120
};

interface AnonymousDaemonState {
  connected: boolean;
  activeSessions: number;
  hasError: boolean;
  loading: boolean;
}

/**
 * Reads and live-refreshes the daemon status of a login-free service.
 *
 * Connectivity flows through the unified activity registry, which is authoritative once ready
 * (Docker availability is reconciled independently of session activity - see
 * DaemonConnectivityReconciler). Before that first snapshot lands, it falls back to the fetched
 * status instead of guessing - NOT an `||`, since a stale-true fetched value could otherwise mask a
 * fresh registry false.
 *
 * `service` must be a stable reference (a module-level table), since it identifies both the fetch
 * and the hub subscriptions.
 */
function useAnonymousDaemonStatus(
  service: AnonymousDaemonService,
  onLoadError: () => void
): AnonymousDaemonState {
  const { on, off, isConnected } = useSignalR();
  const activity = useActivityStatus();
  const [status, setStatus] = useState<DaemonStatusDto | null>(null);
  const [hasError, setHasError] = useState(false);
  const [loading, setLoading] = useState(true);

  // Held in a ref so a parent re-rendering with a fresh callback does not re-run the fetch and the
  // hub subscriptions below.
  const onLoadErrorRef = useRef(onLoadError);
  useEffect(() => {
    onLoadErrorRef.current = onLoadError;
  }, [onLoadError]);

  const loadStatus = useCallback(async () => {
    try {
      const data = await service.loadStatus();
      setStatus(data);
      setHasError(false);
    } catch {
      setHasError(true);
      setStatus(OFFLINE_STATUS);
      onLoadErrorRef.current();
    }
  }, [service]);

  useEffect(() => {
    loadStatus().finally(() => setLoading(false));
  }, [loadStatus]);

  // Refresh when the daemon reports a status change over its own hub
  const refreshEvents = service.refreshEvents;
  useEffect(() => {
    const handleUpdate = () => {
      loadStatus();
    };
    refreshEvents.forEach((event) => on(event, handleUpdate));
    return () => {
      refreshEvents.forEach((event) => off(event, handleUpdate));
    };
  }, [on, off, loadStatus, refreshEvents]);

  // Refresh data when SignalR reconnects (catches events missed during disconnect)
  useReconnectRefetch(isConnected, loadStatus);

  return {
    connected: activity.isActiveOrFallback(
      'integration',
      service.integrationKey,
      'connected',
      status?.dockerAvailable ?? false
    ),
    activeSessions: status?.activeSessions ?? 0,
    hasError,
    loading
  };
}

interface AnonymousDaemonStatusProps {
  service: AnonymousDaemonService;
  copy: AnonymousDaemonCopy;
  onError?: (message: string) => void;
}

/**
 * Login-free variant of the daemon card. These services download public CDN content and need no
 * account, so the card reports Docker availability plus an active session count and carries no
 * sign-in control. A service supplies only its table and its copy.
 */
const AnonymousDaemonStatus: React.FC<AnonymousDaemonStatusProps> = ({
  service,
  copy,
  onError
}) => {
  const loadErrorMessage = copy.loadError;
  const handleLoadError = useCallback(
    () => onError?.(loadErrorMessage),
    [onError, loadErrorMessage]
  );
  const { connected, activeSessions, hasError, loading } = useAnonymousDaemonStatus(
    service,
    handleLoadError
  );

  return (
    <DaemonStatusCard
      accordionId={service.accordionId}
      title={copy.title}
      description={copy.summary}
      icon={service.icon}
      iconColor={service.iconColor}
      help={copy.help}
      loading={loading}
      loadingMessage={copy.loadingStatus}
      hasError={hasError}
      errorMessage={loadErrorMessage}
      connected={connected}
      connectedLabel={copy.connected}
      notConnectedLabel={copy.notConnected}
      headline={connected ? copy.availableHeadline : copy.notConnected}
      detail={connected ? copy.availableDetail : copy.unavailableDetail}
      readout={
        <span className="text-xs text-themed-muted">{copy.sessionCount(activeSessions)}</span>
      }
    />
  );
};

export default AnonymousDaemonStatus;
