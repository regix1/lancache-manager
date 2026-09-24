import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import { useMockMode } from '@contexts/useMockMode';
import { getErrorMessage } from '@utils/error';
import DaemonStatusCard from './DaemonStatusCard';
import type { AnonymousDaemonCopy, AnonymousDaemonService } from './daemonStatus.types';
import type { DaemonStatusDto } from '../../../../types';

/** Mock mode's answer: a definite offline state rather than a real machine's container state. */
const OFFLINE_STATUS: DaemonStatusDto = {
  dockerAvailable: false,
  activeSessions: 0,
  maxSessionsPerUser: 1,
  sessionTimeoutMinutes: 120
};

interface AnonymousDaemonState {
  connected: boolean;
  activeSessions: number;
  loadError: string | null;
  loading: boolean;
  loadStatus: () => Promise<void>;
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
function useAnonymousDaemonStatus(service: AnonymousDaemonService): AnonymousDaemonState {
  const { on, off, isConnected } = useSignalR();
  const { mockMode } = useMockMode();
  const activity = useActivityStatus();
  const [status, setStatus] = useState<DaemonStatusDto | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  // Mount, daemon events, reconnect and Retry can overlap; only the newest read writes the card.
  const statusRequestRef = useRef(0);

  const loadStatus = useCallback(async () => {
    const request = ++statusRequestRef.current;
    // Mock mode reports the daemon as not connected, the same answer XboxDaemonStatus gives there,
    // rather than a real machine's container state.
    if (mockMode) {
      setStatus(OFFLINE_STATUS);
      setLoadError(null);
      setLoading(false);
      return;
    }
    try {
      const data = await service.loadStatus();
      if (request !== statusRequestRef.current) return;
      setStatus(data);
      setLoadError(null);
    } catch (error: unknown) {
      if (request !== statusRequestRef.current) return;
      setLoadError(getErrorMessage(error));
    } finally {
      // Only the newest read ends loading: a superseded mount read would otherwise show the card
      // with no status read yet, and the newer read has to end the loading the mount read started.
      if (request === statusRequestRef.current) {
        setLoading(false);
      }
    }
  }, [mockMode, service]);

  useEffect(() => {
    void loadStatus();
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
    loadError,
    loading,
    loadStatus
  };
}

interface AnonymousDaemonStatusProps {
  service: AnonymousDaemonService;
  copy: AnonymousDaemonCopy;
}

/**
 * Login-free variant of the daemon card. These services download public CDN content and need no
 * account, so the card reports Docker availability plus an active session count and carries no
 * sign-in control. A service supplies only its table and its copy.
 */
const AnonymousDaemonStatus: React.FC<AnonymousDaemonStatusProps> = ({ service, copy }) => {
  const { connected, activeSessions, loadError, loading, loadStatus } =
    useAnonymousDaemonStatus(service);

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
      loadError={loadError}
      loadErrorTitle={copy.loadError}
      onRetry={() => void loadStatus()}
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
