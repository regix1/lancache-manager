import React, { useState, useEffect, useCallback } from 'react';
import {
  Container,
  Play,
  StopCircle,
  Ban,
  Shield,
  CheckCircle,
  AlertTriangle,
  Clock,
  Loader2,
  RefreshCw,
  Network,
  Monitor,
  User,
  ChevronDown,
  ChevronUp,
  Gamepad2,
  XCircle
} from 'lucide-react';
import { Card, CardContent } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import { Pagination } from '@components/ui/Pagination';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Checkbox } from '@components/ui/Checkbox';
import ApiService, {
  PrefillSessionDto,
  DaemonSessionDto,
  BannedSteamUserDto,
  PrefillHistoryEntryDto
} from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useSignalR } from '@contexts/SignalRContext';
import { cleanIpAddress } from '@components/features/user/types';
import type { DaemonSessionCreatedEvent, DaemonSessionUpdatedEvent, DaemonSessionTerminatedEvent, PrefillHistoryUpdatedEvent } from '@contexts/SignalRContext/types';

interface PrefillSessionsSectionProps {
  isAuthenticated: boolean;
  authMode: string;
  mockMode: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
}

// Helper component for formatted timestamps
const FormattedTimestamp: React.FC<{ timestamp: string | undefined }> = ({ timestamp }) => {
  const formattedTime = useFormattedDateTime(timestamp);
  return <>{formattedTime}</>;
};

// Helper function for formatting bytes
const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${parseFloat((bytes / Math.pow(k, i)).toFixed(1))} ${sizes[i]}`;
};

// Prefill history status badge - derives effective status from status + completedAtUtc
const HistoryStatusBadge: React.FC<{ status: string; completedAtUtc?: string }> = ({ status, completedAtUtc }) => {
  // Derive the effective status: if completedAtUtc is set but status is still InProgress,
  // the download actually completed (race condition with status update)
  const getEffectiveStatus = () => {
    const normalizedStatus = status.toLowerCase();

    console.log(`[HistoryStatusBadge] status=${status}, normalizedStatus=${normalizedStatus}, completedAtUtc=${completedAtUtc}`);

    // If completedAtUtc is set but status is still InProgress, treat as Completed
    if (completedAtUtc && normalizedStatus === 'inprogress') {
      console.log('[HistoryStatusBadge] Overriding InProgress -> Completed due to completedAtUtc');
      return 'completed';
    }

    return normalizedStatus;
  };

  const effectiveStatus = getEffectiveStatus();

  const getStatusStyle = () => {
    switch (effectiveStatus) {
      case 'completed':
        return { bg: 'var(--theme-icon-green)', text: '#fff' };
      case 'inprogress':
        return { bg: 'var(--theme-primary)', text: '#fff' };
      case 'failed':
      case 'error':
        return { bg: 'var(--theme-icon-red)', text: '#fff' };
      case 'cancelled':
        return { bg: 'var(--theme-icon-orange)', text: '#fff' };
      default:
        return { bg: 'var(--theme-bg-tertiary)', text: 'var(--theme-text-secondary)' };
    }
  };

  const colors = getStatusStyle();
  
  // Display capitalized version
  const getDisplayStatus = () => {
    switch (effectiveStatus) {
      case 'completed': return 'Completed';
      case 'inprogress': return 'In Progress';
      case 'failed': return 'Failed';
      case 'error': return 'Error';
      case 'cancelled': return 'Cancelled';
      default: return status;
    }
  };

  return (
    <span
      className="px-1.5 py-0.5 rounded text-[10px] font-medium"
      style={{ backgroundColor: colors.bg, color: colors.text }}
    >
      {getDisplayStatus()}
    </span>
  );
};

// Status badge component
const StatusBadge: React.FC<{ status: string; isLive?: boolean }> = ({ status, isLive }) => {
  const getStatusColor = (status: string) => {
    switch (status.toLowerCase()) {
      case 'active':
      case 'authenticated':
        return { bg: 'var(--theme-icon-green)', text: '#fff' };
      case 'pendingauth':
      case 'awaitingcredential':
        return { bg: 'var(--theme-icon-orange)', text: '#fff' };
      case 'terminated':
      case 'expired':
        return { bg: 'var(--theme-icon-red)', text: '#fff' };
      case 'orphaned':
        return { bg: 'var(--theme-icon-purple)', text: '#fff' };
      case 'cleaned':
        return { bg: 'var(--theme-text-muted)', text: '#fff' };
      default:
        return { bg: 'var(--theme-bg-tertiary)', text: 'var(--theme-text-secondary)' };
    }
  };

  const colors = getStatusColor(status);

  return (
    <div className="flex items-center gap-1.5">
      <span
        className="px-2 py-0.5 rounded text-xs font-medium"
        style={{ backgroundColor: colors.bg, color: colors.text }}
      >
        {status}
      </span>
      {isLive && (
        <Tooltip content="Session is currently active in memory">
          <span
            className="px-1.5 py-0.5 rounded text-xs font-medium flex items-center gap-1 icon-bg-green icon-green"
          >
            <Play className="w-3 h-3" />
            Live
          </span>
        </Tooltip>
      )}
    </div>
  );
};

const PrefillSessionsSection: React.FC<PrefillSessionsSectionProps> = ({
  isAuthenticated,
  onError,
  onSuccess
}) => {
  const { on, off } = useSignalR();

  // Sessions state
  const [sessions, setSessions] = useState<PrefillSessionDto[]>([]);
  const [activeSessions, setActiveSessions] = useState<DaemonSessionDto[]>([]);
  const [loadingSessions, setLoadingSessions] = useState(true);
  const [page, setPage] = useState(1);
  const [pageSize] = useState(20);
  const [totalCount, setTotalCount] = useState(0);
  const [statusFilter, setStatusFilter] = useState<string>('');

  // Bans state
  const [bans, setBans] = useState<BannedSteamUserDto[]>([]);
  const [loadingBans, setLoadingBans] = useState(true);
  const [includeLifted, setIncludeLifted] = useState(false);

  // Action states
  const [terminatingSession, setTerminatingSession] = useState<string | null>(null);
  const [terminatingAll, setTerminatingAll] = useState(false);
  const [liftingBan, setLiftingBan] = useState<number | null>(null);
  const [banningSession, setBanningSession] = useState<string | null>(null);

  // Modal states
  const [terminateAllConfirm, setTerminateAllConfirm] = useState(false);
  const [banConfirm, setBanConfirm] = useState<{ sessionId: string; reason: string } | null>(null);
  const [liftBanConfirm, setLiftBanConfirm] = useState<BannedSteamUserDto | null>(null);

  // Prefill history states
  const [expandedHistory, setExpandedHistory] = useState<Set<string>>(new Set());
  const [historyData, setHistoryData] = useState<Record<string, PrefillHistoryEntryDto[]>>({});
  const [loadingHistory, setLoadingHistory] = useState<Set<string>>(new Set());
  const [historyPage, setHistoryPage] = useState<Record<string, number>>({});
  const historyPageSize = 5;

  // Load sessions and pre-fetch history for summary badges
  const loadSessions = useCallback(async () => {
    setLoadingSessions(true);
    console.log('[PrefillSessions] loadSessions - Starting...');
    try {
      const [sessionsRes, activeRes] = await Promise.all([
        ApiService.getPrefillSessions(page, pageSize, statusFilter || undefined),
        ApiService.getActivePrefillSessions()
      ]);
      console.log('[PrefillSessions] loadSessions - Sessions:', sessionsRes.sessions);
      console.log('[PrefillSessions] loadSessions - Active sessions:', activeRes);
      setSessions(sessionsRes.sessions);
      setTotalCount(sessionsRes.totalCount);
      setActiveSessions(activeRes);

      // Pre-fetch history for all sessions to show summary badges immediately
      // Fetch in parallel but don't block the UI
      const historyPromises = sessionsRes.sessions.map(async (session) => {
        try {
          const history = await ApiService.getPrefillSessionHistory(session.sessionId);
          console.log(`[PrefillSessions] History for ${session.sessionId}:`, history);
          return { sessionId: session.sessionId, history };
        } catch (err) {
          console.error(`[PrefillSessions] Failed to load history for ${session.sessionId}:`, err);
          return { sessionId: session.sessionId, history: [] };
        }
      });

      // Also fetch history for active sessions
      const activeHistoryPromises = activeRes.map(async (session) => {
        try {
          const history = await ApiService.getPrefillSessionHistory(session.id);
          console.log(`[PrefillSessions] Active session history for ${session.id}:`, history);
          return { sessionId: session.id, history };
        } catch (err) {
          console.error(`[PrefillSessions] Failed to load history for active session ${session.id}:`, err);
          return { sessionId: session.id, history: [] };
        }
      });

      // Combine results without blocking
      Promise.all([...historyPromises, ...activeHistoryPromises]).then((results) => {
        console.log('[PrefillSessions] All history results:', results);
        const newHistoryData: Record<string, PrefillHistoryEntryDto[]> = {};
        results.forEach(({ sessionId, history }) => {
          newHistoryData[sessionId] = history;
          // Debug: Log each entry's bytes
          if (history.length > 0) {
            const totalBytes = history.reduce((sum, e) => sum + Math.max(e.bytesDownloaded, e.totalBytes || 0), 0);
            console.log(`[PrefillSessions] Session ${sessionId} - ${history.length} entries, totalBytes: ${totalBytes}`);
            history.forEach((e, i) => {
              console.log(`  Entry ${i}: appId=${e.appId}, bytesDownloaded=${e.bytesDownloaded}, totalBytes=${e.totalBytes}, status=${e.status}, completedAtUtc=${e.completedAtUtc}`);
            });
          }
        });
        setHistoryData(prev => ({ ...prev, ...newHistoryData }));
      });
    } catch (error) {
      console.error('[PrefillSessions] loadSessions error:', error);
      onError(getErrorMessage(error));
    } finally {
      setLoadingSessions(false);
    }
  }, [page, pageSize, statusFilter, onError]);

  // Load bans
  const loadBans = useCallback(async () => {
    setLoadingBans(true);
    try {
      const bansRes = await ApiService.getSteamBans(includeLifted);
      setBans(bansRes);
    } catch (error) {
      onError(getErrorMessage(error));
    } finally {
      setLoadingBans(false);
    }
  }, [includeLifted, onError]);

  // Load prefill history for a session
  const loadHistory = useCallback(async (sessionId: string) => {
    console.log(`[PrefillSessions] loadHistory - Loading for session: ${sessionId}`);
    setLoadingHistory(prev => new Set(prev).add(sessionId));
    try {
      const history = await ApiService.getPrefillSessionHistory(sessionId);
      console.log(`[PrefillSessions] loadHistory - Got ${history.length} entries for ${sessionId}:`, history);
      // Debug each entry
      history.forEach((e, i) => {
        console.log(`  Entry ${i}: appId=${e.appId}, appName=${e.appName}, bytesDownloaded=${e.bytesDownloaded}, totalBytes=${e.totalBytes}, status=${e.status}, completedAtUtc=${e.completedAtUtc}`);
      });
      const totalBytes = history.reduce((sum, e) => sum + Math.max(e.bytesDownloaded, e.totalBytes || 0), 0);
      console.log(`[PrefillSessions] loadHistory - Total bytes for ${sessionId}: ${totalBytes}`);
      setHistoryData(prev => ({ ...prev, [sessionId]: history }));
    } catch (error) {
      console.error(`[PrefillSessions] loadHistory error for ${sessionId}:`, error);
      onError(getErrorMessage(error));
    } finally {
      setLoadingHistory(prev => {
        const next = new Set(prev);
        next.delete(sessionId);
        return next;
      });
    }
  }, [onError]);

  // Toggle history expansion
  const toggleHistory = useCallback((sessionId: string) => {
    setExpandedHistory(prev => {
      const next = new Set(prev);
      if (next.has(sessionId)) {
        next.delete(sessionId);
      } else {
        next.add(sessionId);
        // Load history if not already loaded
        if (!historyData[sessionId]) {
          loadHistory(sessionId);
        }
      }
      return next;
    });
  }, [historyData, loadHistory]);

  // Initial load
  useEffect(() => {
    loadSessions();
  }, [loadSessions]);

  useEffect(() => {
    loadBans();
  }, [loadBans]);

  // Debug: Log when historyData changes
  useEffect(() => {
    console.log('[PrefillSessions] historyData updated:', historyData);
    Object.entries(historyData).forEach(([sessionId, entries]) => {
      const totalBytes = entries.reduce((sum, e) => sum + Math.max(e.bytesDownloaded, e.totalBytes || 0), 0);
      console.log(`[PrefillSessions] Session ${sessionId}: ${entries.length} entries, ${totalBytes} bytes total`);
    });
  }, [historyData]);

  // SignalR subscriptions for real-time updates
  useEffect(() => {
    const handleSessionCreated = (session: DaemonSessionCreatedEvent) => {
      setActiveSessions(prev => {
        // Check if session already exists
        if (prev.some(s => s.id === session.id)) return prev;
        return [...prev, session as DaemonSessionDto];
      });
    };

    const handleSessionUpdated = (session: DaemonSessionUpdatedEvent) => {
      setActiveSessions(prev =>
        prev.map(s => s.id === session.id ? session as DaemonSessionDto : s)
      );
    };

    const handleSessionTerminated = (event: DaemonSessionTerminatedEvent) => {
      setActiveSessions(prev => prev.filter(s => s.id !== event.sessionId));
      // Reload session history to show the terminated session
      loadSessions();
    };

    const handlePrefillHistoryUpdated = (event: PrefillHistoryUpdatedEvent) => {
      console.log('[PrefillSessions] PrefillHistoryUpdated event received:', event);
      // Reload history for this session - both for expanded views and summary badges
      // Since history is pre-loaded for summary badges, we should update it on any history change
      loadHistory(event.sessionId);
    };

    on('DaemonSessionCreated', handleSessionCreated);
    on('DaemonSessionUpdated', handleSessionUpdated);
    on('DaemonSessionTerminated', handleSessionTerminated);
    on('PrefillHistoryUpdated', handlePrefillHistoryUpdated);

    return () => {
      off('DaemonSessionCreated', handleSessionCreated);
      off('DaemonSessionUpdated', handleSessionUpdated);
      off('DaemonSessionTerminated', handleSessionTerminated);
      off('PrefillHistoryUpdated', handlePrefillHistoryUpdated);
    };
  }, [on, off, loadSessions, expandedHistory, loadHistory]);

  // Terminate a single session
  const handleTerminateSession = async (sessionId: string) => {
    setTerminatingSession(sessionId);
    try {
      await ApiService.terminatePrefillSession(sessionId, 'Terminated by admin');
      onSuccess('Session terminated');
      await loadSessions();
    } catch (error) {
      onError(getErrorMessage(error));
    } finally {
      setTerminatingSession(null);
    }
  };

  // Terminate all sessions
  const handleTerminateAll = async () => {
    setTerminatingAll(true);
    try {
      const result = await ApiService.terminateAllPrefillSessions('Bulk termination by admin');
      onSuccess(result.message);
      setTerminateAllConfirm(false);
      await loadSessions();
    } catch (error) {
      onError(getErrorMessage(error));
    } finally {
      setTerminatingAll(false);
    }
  };

  // Ban user by session
  const handleBanBySession = async () => {
    if (!banConfirm) return;
    setBanningSession(banConfirm.sessionId);
    try {
      await ApiService.banSteamUserBySession(banConfirm.sessionId, banConfirm.reason || undefined);
      onSuccess('User banned successfully');
      setBanConfirm(null);
      await Promise.all([loadSessions(), loadBans()]);
    } catch (error) {
      onError(getErrorMessage(error));
    } finally {
      setBanningSession(null);
    }
  };

  // Lift a ban
  const handleLiftBan = async (banId: number) => {
    setLiftingBan(banId);
    try {
      await ApiService.liftSteamBan(banId);
      onSuccess('Ban lifted successfully');
      setLiftBanConfirm(null);
      await loadBans();
    } catch (error) {
      onError(getErrorMessage(error));
    } finally {
      setLiftingBan(null);
    }
  };

  const totalPages = Math.ceil(totalCount / pageSize);

  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-prefill-sessions"
      aria-labelledby="tab-prefill-sessions"
    >
      {/* Section Header */}
      <div className="mb-6 flex items-center justify-between">
        <div>
          <h2 className="text-xl font-semibold text-themed-primary mb-1">
            Prefill Sessions
          </h2>
          <p className="text-themed-secondary text-sm">
            Manage Steam Prefill daemon sessions and user bans
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="subtle"
            size="sm"
            onClick={() => { loadSessions(); loadBans(); }}
            disabled={loadingSessions || loadingBans}
          >
            <RefreshCw className={`w-4 h-4 ${loadingSessions || loadingBans ? 'animate-spin' : ''}`} />
          </Button>
          {isAuthenticated && activeSessions.length > 0 && (
            <Button
              variant="filled"
              color="red"
              size="sm"
              onClick={() => setTerminateAllConfirm(true)}
              disabled={terminatingAll}
            >
              <StopCircle className="w-4 h-4" />
              End All ({activeSessions.length})
            </Button>
          )}
        </div>
      </div>

      {/* Active Sessions Summary */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-6">
        <Card>
          <CardContent className="py-4 flex items-center gap-4">
            <div className="w-12 h-12 rounded-lg flex items-center justify-center icon-bg-green">
              <Play className="w-6 h-6 icon-green" />
            </div>
            <div>
              <div className="text-2xl font-bold text-themed-primary">{activeSessions.length}</div>
              <div className="text-sm text-themed-muted">Active Sessions</div>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardContent className="py-4 flex items-center gap-4">
            <div className="w-12 h-12 rounded-lg flex items-center justify-center icon-bg-blue">
              <Container className="w-6 h-6 icon-primary" />
            </div>
            <div>
              <div className="text-2xl font-bold text-themed-primary">{totalCount}</div>
              <div className="text-sm text-themed-muted">Total Sessions</div>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardContent className="py-4 flex items-center gap-4">
            <div className="w-12 h-12 rounded-lg flex items-center justify-center icon-bg-red">
              <Ban className="w-6 h-6 icon-red" />
            </div>
            <div>
              <div className="text-2xl font-bold text-themed-primary">
                {bans.filter(b => b.isActive).length}
              </div>
              <div className="text-sm text-themed-muted">Active Bans</div>
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Live Sessions */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-green)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            Live Sessions ({activeSessions.length})
          </h3>
        </div>

        {loadingSessions ? (
          <Card>
            <CardContent className="py-8 flex items-center justify-center">
              <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
              <span className="ml-2 text-themed-muted">Loading sessions...</span>
            </CardContent>
          </Card>
        ) : activeSessions.length === 0 ? (
          <Card>
            <CardContent className="py-8 text-center text-themed-muted">
              <Container className="w-12 h-12 mx-auto mb-3 opacity-50" />
              <p className="mb-2">No active prefill sessions</p>
              <p className="text-sm">
                Sessions appear here when users start Steam Prefill
              </p>
            </CardContent>
          </Card>
        ) : (
          <div className="grid gap-3">
            {activeSessions.map(session => {
              const sessionHistory = historyData[session.id];
              const totalBytesFromHistory = sessionHistory
                ? sessionHistory.reduce((sum, e) => sum + Math.max(e.bytesDownloaded, e.totalBytes || 0), 0)
                : 0;
              const gamesCount = sessionHistory?.length || 0;

              // Debug: Log render-time calculations
              console.log(`[PrefillSessions] Rendering active session ${session.id}:`, {
                hasHistory: !!sessionHistory,
                historyLength: sessionHistory?.length || 0,
                totalBytesFromHistory,
                gamesCount,
                historyEntries: sessionHistory
              });

              return (
              <Card key={session.id}>
                <CardContent className="py-4">
                  {/* Main content row */}
                  <div className="flex items-start justify-between gap-4">
                    {/* Left: User info and session details */}
                    <div className="flex items-start gap-3 flex-1 min-w-0">
                      {/* Status indicator with pulsing animation for active */}
                      <div
                        className={`w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0 ${
                          session.isPrefilling
                            ? 'icon-bg-blue'
                            : 'icon-bg-green'
                        }`}
                      >
                        {session.isPrefilling ? (
                          <Loader2 className="w-5 h-5 animate-spin icon-primary" />
                        ) : (
                          <Play className="w-5 h-5 icon-green" />
                        )}
                      </div>

                      <div className="flex-1 min-w-0">
                        {/* Primary: Steam username or Container name */}
                        <div className="flex items-center gap-2 flex-wrap">
                          {session.steamUsername ? (
                            <span className="text-sm font-semibold text-themed-primary flex items-center gap-1.5">
                              <User className="w-3.5 h-3.5 service-steam" />
                              {session.steamUsername}
                            </span>
                          ) : (
                            <Tooltip content={session.containerName}>
                              <span className="font-mono text-sm text-themed-secondary">
                                {session.containerName}
                              </span>
                            </Tooltip>
                          )}
                          <StatusBadge status={session.status} isLive />
                        </div>

                        {/* Prefilling status - shown prominently when active */}
                        {session.isPrefilling && (
                          <div className="flex items-center gap-2 mt-2 px-2 py-1.5 rounded-md bg-themed-tertiary">
                            <span className="text-sm font-medium icon-primary">
                              {session.currentAppName || 'Loading...'}
                            </span>
                            {(session.totalBytesTransferred ?? 0) > 0 && (
                              <span className="text-xs text-themed-muted">
                                {formatBytes(session.totalBytesTransferred!)}
                              </span>
                            )}
                          </div>
                        )}

                        {/* Secondary: Container name */}
                        <div className="flex items-center gap-1.5 mt-1.5 text-xs text-themed-muted">
                          <Container className="w-3 h-3 flex-shrink-0" />
                          <span className="font-mono break-all">
                            {session.containerName}
                          </span>
                        </div>

                        {/* Compact info row */}
                        <div className="flex items-center gap-3 mt-1.5 text-xs text-themed-muted flex-wrap">
                          <span className="flex items-center gap-1">
                            <Clock className="w-3 h-3" />
                            <FormattedTimestamp timestamp={session.createdAt} />
                          </span>
                          {session.ipAddress && (
                            <span className="flex items-center gap-1">
                              <Network className="w-3 h-3" />
                              <span className="font-mono">{cleanIpAddress(session.ipAddress)}</span>
                            </span>
                          )}
                          {(session.operatingSystem || session.browser) && (
                            <span className="flex items-center gap-1">
                              <Monitor className="w-3 h-3" />
                              {session.operatingSystem || session.browser}
                            </span>
                          )}
                        </div>
                      </div>
                    </div>

                    {/* Right: Stats and actions */}
                    <div className="flex items-center gap-3 flex-shrink-0">
                      {/* Stats badges */}
                      {(gamesCount > 0 || totalBytesFromHistory > 0 || (!session.isPrefilling && (session.totalBytesTransferred ?? 0) > 0)) && (
                        <div className="flex items-center gap-2">
                          {gamesCount > 0 && (
                            <Tooltip content={`${gamesCount} game${gamesCount !== 1 ? 's' : ''} prefilled`}>
                              <span className="px-2 py-1 rounded-md text-xs font-medium flex items-center gap-1.5 icon-bg-blue icon-primary">
                                <Gamepad2 className="w-3.5 h-3.5" />
                                {gamesCount}
                              </span>
                            </Tooltip>
                          )}
                          {(totalBytesFromHistory > 0 || (!session.isPrefilling && (session.totalBytesTransferred ?? 0) > 0)) && (
                            <Tooltip content="Total data downloaded">
                              <span className="px-2 py-1 rounded-md text-xs font-medium icon-bg-green icon-green">
                                {formatBytes(totalBytesFromHistory || session.totalBytesTransferred || 0)}
                              </span>
                            </Tooltip>
                          )}
                        </div>
                      )}

                      {/* Action buttons */}
                      <div className="flex items-center gap-1">
                        <Button
                          variant="subtle"
                          size="sm"
                          onClick={() => toggleHistory(session.id)}
                        >
                          {loadingHistory.has(session.id) ? (
                            <Loader2 className="w-4 h-4 animate-spin" />
                          ) : expandedHistory.has(session.id) ? (
                            <ChevronUp className="w-4 h-4" />
                          ) : (
                            <ChevronDown className="w-4 h-4" />
                          )}
                        </Button>
                        {isAuthenticated && (
                          <>
                            <Tooltip content="Ban this Steam user">
                            <Button
                              variant="subtle"
                              size="sm"
                              color="red"
                              onClick={() => setBanConfirm({ sessionId: session.id, reason: '' })}
                              disabled={banningSession === session.id}
                            >
                              {banningSession === session.id ? (
                                <Loader2 className="w-4 h-4 animate-spin" />
                              ) : (
                                <Ban className="w-4 h-4" />
                              )}
                            </Button>
                          </Tooltip>
                          <Tooltip content="Terminate session">
                            <Button
                              variant="subtle"
                              size="sm"
                              color="red"
                              onClick={() => handleTerminateSession(session.id)}
                              disabled={terminatingSession === session.id}
                            >
                              {terminatingSession === session.id ? (
                                <Loader2 className="w-4 h-4 animate-spin" />
                              ) : (
                                <StopCircle className="w-4 h-4" />
                              )}
                            </Button>
                          </Tooltip>
                        </>
                      )}
                      </div>
                    </div>
                  </div>

                  {/* Expandable prefill history */}
                  {expandedHistory.has(session.id) && (
                    <div
                      className="mt-4 pt-4 border-t border-themed-primary"
                    >
                      <div className="flex items-center gap-2 mb-3">
                        <Gamepad2 className="w-4 h-4 text-themed-muted" />
                        <span className="text-sm font-medium text-themed-secondary">Prefill History</span>
                      </div>

                      {loadingHistory.has(session.id) ? (
                        <div className="flex items-center gap-2 py-4 justify-center">
                          <Loader2 className="w-4 h-4 animate-spin text-themed-muted" />
                          <span className="text-sm text-themed-muted">Loading history...</span>
                        </div>
                      ) : !historyData[session.id] || historyData[session.id].length === 0 ? (
                        <div className="text-center py-4 text-sm text-themed-muted">
                          No prefill history yet
                        </div>
                      ) : (() => {
                        const allEntries = historyData[session.id];
                        const currentPage = historyPage[session.id] || 1;
                        const totalPages = Math.ceil(allEntries.length / historyPageSize);
                        const startIdx = (currentPage - 1) * historyPageSize;
                        const paginatedEntries = allEntries.slice(startIdx, startIdx + historyPageSize);
                        const totalBytes = allEntries.reduce((sum, e) => sum + Math.max(e.bytesDownloaded, e.totalBytes || 0), 0);

                        return (
                          <>
                            {/* Summary stats */}
                            <div className="flex items-center gap-4 mb-3 text-xs text-themed-muted">
                              <span>{allEntries.length} game{allEntries.length !== 1 ? 's' : ''} prefilled</span>
                              {totalBytes > 0 && (
                                <span>Total: {formatBytes(totalBytes)}</span>
                              )}
                            </div>

                            <div className="space-y-2">
                              {paginatedEntries.map(entry => (
                                <div
                                  key={entry.id}
                                  className="flex items-center justify-between gap-3 p-2 rounded bg-themed-tertiary"
                                >
                                  <div className="flex items-center gap-2 min-w-0 flex-1">
                                    <Gamepad2 className="w-4 h-4 text-themed-muted flex-shrink-0" />
                                    <div className="min-w-0 flex-1">
                                      <div className="flex items-center gap-2">
                                        <span className="text-sm font-medium text-themed-primary truncate">
                                          {entry.appName || `App ${entry.appId}`}
                                        </span>
                                        <HistoryStatusBadge status={entry.status} completedAtUtc={entry.completedAtUtc} />
                                      </div>
                                      <div className="flex items-center gap-3 text-[10px] text-themed-muted mt-0.5">
                                        <span>Started: <FormattedTimestamp timestamp={entry.startedAtUtc} /></span>
                                        {entry.completedAtUtc && (
                                          <span>Completed: <FormattedTimestamp timestamp={entry.completedAtUtc} /></span>
                                        )}
                                        {(entry.bytesDownloaded > 0 || entry.totalBytes > 0) && (
                                          <span>
                                            {entry.totalBytes > 0 && entry.bytesDownloaded !== entry.totalBytes
                                              ? `${formatBytes(entry.bytesDownloaded)} / ${formatBytes(entry.totalBytes)}`
                                              : formatBytes(entry.bytesDownloaded || entry.totalBytes)}
                                          </span>
                                        )}
                                      </div>
                                      {entry.errorMessage && (
                                        <div className="flex items-center gap-1 mt-1 text-[10px] icon-red">
                                          <XCircle className="w-3 h-3" />
                                          <span className="truncate">{entry.errorMessage}</span>
                                        </div>
                                      )}
                                    </div>
                                  </div>
                                </div>
                              ))}
                            </div>

                            {/* Pagination */}
                            {totalPages > 1 && (
                              <div className="mt-3 flex justify-center">
                                <Pagination
                                  currentPage={currentPage}
                                  totalPages={totalPages}
                                  totalItems={allEntries.length}
                                  itemsPerPage={historyPageSize}
                                  onPageChange={(newPage) => setHistoryPage(prev => ({ ...prev, [session.id]: newPage }))}
                                  itemLabel="games"
                                  compact
                                />
                              </div>
                            )}
                          </>
                        );
                      })()}
                    </div>
                  )}
                </CardContent>
              </Card>
              );
            })}
          </div>
        )}
      </div>

      {/* Session History */}
      <div className="mb-8">
        <div className="flex items-center justify-between mb-4">
          <div className="flex items-center gap-2">
            <div className="w-1 h-5 rounded-full bg-[var(--theme-primary)]" />
            <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
              Session History ({totalCount})
            </h3>
          </div>

          <div className="flex items-center gap-2">
            <EnhancedDropdown
              options={[
                { value: '', label: 'All Statuses' },
                { value: 'Active', label: 'Active' },
                { value: 'Terminated', label: 'Terminated' },
                { value: 'Orphaned', label: 'Orphaned' },
                { value: 'Cleaned', label: 'Cleaned' }
              ] as DropdownOption[]}
              value={statusFilter}
              onChange={(value) => { setStatusFilter(value); setPage(1); }}
              placeholder="All Statuses"
              compactMode
              className="min-w-[140px]"
              dropdownWidth="160px"
            />
          </div>
        </div>

        {loadingSessions ? (
          <Card>
            <CardContent className="py-8 flex items-center justify-center">
              <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
              <span className="ml-2 text-themed-muted">Loading...</span>
            </CardContent>
          </Card>
        ) : sessions.length === 0 ? (
          <Card>
            <CardContent className="py-8 text-center text-themed-muted">
              <p>No sessions found</p>
            </CardContent>
          </Card>
        ) : (
          <>
            <div className="space-y-3">
              {sessions.map(session => {
                const sessionHistory = historyData[session.sessionId];
                const totalBytesFromHistory = sessionHistory
                  ? sessionHistory.reduce((sum, e) => sum + Math.max(e.bytesDownloaded, e.totalBytes || 0), 0)
                  : 0;
                const gamesCount = sessionHistory?.length || 0;

                // Debug: Log render-time calculations for session history
                console.log(`[PrefillSessions] Rendering session history ${session.sessionId}:`, {
                  sessionId: session.sessionId,
                  hasHistory: !!sessionHistory,
                  historyLength: sessionHistory?.length || 0,
                  totalBytesFromHistory,
                  gamesCount,
                  historyEntries: sessionHistory
                });

                return (
                  <Card key={session.id}>
                    <CardContent className="py-4">
                      {/* Main content row */}
                      <div className="flex items-start justify-between gap-4">
                        {/* Left: User info and session details */}
                        <div className="flex items-start gap-3 flex-1 min-w-0">
                          {/* Status indicator icon */}
                          <div
                            className={`w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0 ${
                              session.isLive
                                ? 'icon-bg-green'
                                : session.status === 'Terminated'
                                  ? 'icon-bg-red'
                                  : 'bg-themed-tertiary'
                            }`}
                          >
                            {session.isLive ? (
                              <Play className="w-5 h-5 icon-green" />
                            ) : session.status === 'Terminated' ? (
                              <StopCircle className="w-5 h-5 icon-red" />
                            ) : (
                              <Container className="w-5 h-5 text-themed-muted" />
                            )}
                          </div>

                          <div className="flex-1 min-w-0">
                            {/* Primary: Steam username or Session ID */}
                            <div className="flex items-center gap-2 flex-wrap">
                              {session.steamUsername ? (
                                <span className="text-sm font-semibold text-themed-primary flex items-center gap-1.5">
                                  <User className="w-3.5 h-3.5 service-steam" />
                                  {session.steamUsername}
                                </span>
                              ) : (
                                <span className="font-mono text-sm text-themed-secondary">
                                  {session.sessionId}
                                </span>
                              )}
                              <StatusBadge status={session.status} isLive={session.isLive} />
                              {session.isAuthenticated && (
                                <Tooltip content="Steam authenticated">
                                  <CheckCircle className="w-4 h-4 icon-green" />
                                </Tooltip>
                              )}
                            </div>

                            {/* Secondary: Container name */}
                            {session.containerName && (
                              <div className="flex items-center gap-1.5 mt-1 text-xs text-themed-muted">
                                <Container className="w-3 h-3 flex-shrink-0" />
                                <span className="font-mono break-all">
                                  {session.containerName}
                                </span>
                              </div>
                            )}

                            {/* Timestamps row */}
                            <div className="flex items-center gap-3 mt-1.5 text-xs text-themed-muted flex-wrap">
                              <span className="flex items-center gap-1">
                                <Clock className="w-3 h-3" />
                                <FormattedTimestamp timestamp={session.createdAtUtc} />
                              </span>
                              {session.endedAtUtc && (
                                <span className="flex items-center gap-1">
                                  → <FormattedTimestamp timestamp={session.endedAtUtc} />
                                </span>
                              )}
                            </div>
                          </div>
                        </div>

                        {/* Right: Stats and actions */}
                        <div className="flex items-center gap-3 flex-shrink-0">
                          {/* Stats badges */}
                          {(gamesCount > 0 || totalBytesFromHistory > 0) && (
                            <div className="flex items-center gap-2">
                              {gamesCount > 0 && (
                                <Tooltip content={`${gamesCount} game${gamesCount !== 1 ? 's' : ''} prefilled`}>
                                  <span className="px-2 py-1 rounded-md text-xs font-medium flex items-center gap-1.5 icon-bg-blue icon-primary">
                                    <Gamepad2 className="w-3.5 h-3.5" />
                                    {gamesCount}
                                  </span>
                                </Tooltip>
                              )}
                              {totalBytesFromHistory > 0 && (
                                <Tooltip content="Total data downloaded">
                                  <span className="px-2 py-1 rounded-md text-xs font-medium icon-bg-green icon-green">
                                    {formatBytes(totalBytesFromHistory)}
                                  </span>
                                </Tooltip>
                              )}
                            </div>
                          )}

                          {/* Action buttons */}
                          <div className="flex items-center gap-1">
                            <Button
                              variant="subtle"
                              size="sm"
                              onClick={() => toggleHistory(session.sessionId)}
                            >
                              {loadingHistory.has(session.sessionId) ? (
                                <Loader2 className="w-4 h-4 animate-spin" />
                              ) : expandedHistory.has(session.sessionId) ? (
                                <ChevronUp className="w-4 h-4" />
                              ) : (
                                <ChevronDown className="w-4 h-4" />
                              )}
                            </Button>

                            {isAuthenticated && session.isLive && (
                              <>
                                {session.steamUsername && (
                                  <Tooltip content="Ban this Steam user">
                                    <Button
                                      variant="subtle"
                                      size="sm"
                                      color="red"
                                      onClick={() => setBanConfirm({ sessionId: session.sessionId, reason: '' })}
                                    >
                                      <Ban className="w-4 h-4" />
                                    </Button>
                                  </Tooltip>
                                )}
                                <Tooltip content="Terminate session">
                                  <Button
                                    variant="subtle"
                                    size="sm"
                                    color="red"
                                    onClick={() => handleTerminateSession(session.sessionId)}
                                    disabled={terminatingSession === session.sessionId}
                                  >
                                    {terminatingSession === session.sessionId ? (
                                      <Loader2 className="w-4 h-4 animate-spin" />
                                    ) : (
                                      <StopCircle className="w-4 h-4" />
                                    )}
                                  </Button>
                                </Tooltip>
                              </>
                            )}
                          </div>
                        </div>
                      </div>

                      {/* Expandable prefill history for session history */}
                      {expandedHistory.has(session.sessionId) && (
                        <div
                          className="mt-4 pt-4 border-t border-themed-primary"
                        >
                          <div className="flex items-center gap-2 mb-3">
                            <Gamepad2 className="w-4 h-4 text-themed-muted" />
                            <span className="text-sm font-medium text-themed-secondary">Prefill History</span>
                          </div>

                          {loadingHistory.has(session.sessionId) ? (
                            <div className="flex items-center gap-2 py-4 justify-center">
                              <Loader2 className="w-4 h-4 animate-spin text-themed-muted" />
                              <span className="text-sm text-themed-muted">Loading history...</span>
                            </div>
                          ) : !sessionHistory || sessionHistory.length === 0 ? (
                            <div className="text-center py-4 text-sm text-themed-muted">
                              No prefill history recorded
                            </div>
                          ) : (() => {
                            const currentPage = historyPage[session.sessionId] || 1;
                            const totalPages = Math.ceil(sessionHistory.length / historyPageSize);
                            const startIdx = (currentPage - 1) * historyPageSize;
                            const paginatedEntries = sessionHistory.slice(startIdx, startIdx + historyPageSize);

                            return (
                              <>
                                {/* Summary stats */}
                                <div className="flex items-center gap-4 mb-3 text-xs text-themed-muted">
                                  <span>{sessionHistory.length} game{sessionHistory.length !== 1 ? 's' : ''} prefilled</span>
                                  {totalBytesFromHistory > 0 && (
                                    <span>Total: {formatBytes(totalBytesFromHistory)}</span>
                                  )}
                                </div>

                                <div className="space-y-2">
                                  {paginatedEntries.map(entry => (
                                    <div
                                      key={entry.id}
                                      className="flex items-center justify-between gap-3 p-2 rounded bg-themed-tertiary"
                                    >
                                      <div className="flex items-center gap-2 min-w-0 flex-1">
                                        <Gamepad2 className="w-4 h-4 text-themed-muted flex-shrink-0" />
                                        <div className="min-w-0 flex-1">
                                          <div className="flex items-center gap-2">
                                            <span className="text-sm font-medium text-themed-primary truncate">
                                              {entry.appName || `App ${entry.appId}`}
                                            </span>
                                            <HistoryStatusBadge status={entry.status} completedAtUtc={entry.completedAtUtc} />
                                          </div>
                                          <div className="flex items-center gap-3 text-[10px] text-themed-muted mt-0.5">
                                            <span>Started: <FormattedTimestamp timestamp={entry.startedAtUtc} /></span>
                                            {entry.completedAtUtc && (
                                              <span>Completed: <FormattedTimestamp timestamp={entry.completedAtUtc} /></span>
                                            )}
                                            {(entry.bytesDownloaded > 0 || entry.totalBytes > 0) && (
                                              <span>
                                                {entry.totalBytes > 0 && entry.bytesDownloaded !== entry.totalBytes
                                                  ? `${formatBytes(entry.bytesDownloaded)} / ${formatBytes(entry.totalBytes)}`
                                                  : formatBytes(entry.bytesDownloaded || entry.totalBytes)}
                                              </span>
                                            )}
                                          </div>
                                          {entry.errorMessage && (
                                            <div className="flex items-center gap-1 mt-1 text-[10px] icon-red">
                                              <XCircle className="w-3 h-3" />
                                              <span className="truncate">{entry.errorMessage}</span>
                                            </div>
                                          )}
                                        </div>
                                      </div>
                                    </div>
                                  ))}
                                </div>

                                {/* Pagination */}
                                {totalPages > 1 && (
                                  <div className="mt-3 flex justify-center">
                                    <Pagination
                                      currentPage={currentPage}
                                      totalPages={totalPages}
                                      totalItems={sessionHistory.length}
                                      itemsPerPage={historyPageSize}
                                      onPageChange={(newPage) => setHistoryPage(prev => ({ ...prev, [session.sessionId]: newPage }))}
                                      itemLabel="games"
                                      compact
                                    />
                                  </div>
                                )}
                              </>
                            );
                          })()}
                        </div>
                      )}
                    </CardContent>
                  </Card>
                );
              })}
            </div>

            {totalPages > 1 && (
              <div className="mt-4 flex justify-center">
                <Pagination
                  currentPage={page}
                  totalPages={totalPages}
                  totalItems={totalCount}
                  itemsPerPage={pageSize}
                  onPageChange={setPage}
                  itemLabel="sessions"
                />
              </div>
            )}
          </>
        )}
      </div>

      {/* Banned Users */}
      <div>
        <div className="flex items-center justify-between mb-4">
          <div className="flex items-center gap-2">
            <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-red)]" />
            <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
              Banned Steam Users ({bans.filter(b => b.isActive).length})
            </h3>
          </div>

          <Checkbox
            label="Show lifted bans"
            checked={includeLifted}
            onChange={(e) => setIncludeLifted(e.target.checked)}
          />
        </div>

        {loadingBans ? (
          <Card>
            <CardContent className="py-8 flex items-center justify-center">
              <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
              <span className="ml-2 text-themed-muted">Loading bans...</span>
            </CardContent>
          </Card>
        ) : bans.length === 0 ? (
          <Card>
            <CardContent className="py-8 text-center text-themed-muted">
              <Shield className="w-12 h-12 mx-auto mb-3 opacity-50" />
              <p className="mb-2">No banned users</p>
              <p className="text-sm">
                Ban users from sessions to prevent them from using prefill
              </p>
            </CardContent>
          </Card>
        ) : (
          <div className="space-y-2">
            {bans.map(ban => (
              <Card key={ban.id}>
                <CardContent className="py-3">
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-4">
                      <div
                        className="w-8 h-8 rounded flex items-center justify-center"
                        style={{
                          backgroundColor: ban.isActive
                            ? 'color-mix(in srgb, var(--theme-icon-red) 20%, transparent)'
                            : 'var(--theme-bg-tertiary)'
                        }}
                      >
                        <Ban
                          className="w-4 h-4"
                          style={{
                            color: ban.isActive ? 'var(--theme-icon-red)' : 'var(--theme-text-muted)'
                          }}
                        />
                      </div>
                      <div>
                        <div className="flex items-center gap-2">
                          <span className="font-mono text-sm text-themed-primary">
                            {ban.username || 'Unknown'}
                          </span>
                          {ban.isActive ? (
                            <span className="px-2 py-0.5 rounded text-xs font-medium bg-[var(--theme-icon-red)] text-white">
                              Active
                            </span>
                          ) : (
                            <span className="px-2 py-0.5 rounded text-xs font-medium bg-[var(--theme-text-muted)] text-white">
                              Lifted
                            </span>
                          )}
                        </div>
                        <div className="flex items-center gap-3 text-xs text-themed-muted mt-1">
                          <span>
                            Banned: <FormattedTimestamp timestamp={ban.bannedAtUtc} />
                          </span>
                          {ban.banReason && (
                            <span className="truncate max-w-xs">
                              Reason: {ban.banReason}
                            </span>
                          )}
                          {ban.expiresAtUtc && (
                            <span>
                              Expires: <FormattedTimestamp timestamp={ban.expiresAtUtc} />
                            </span>
                          )}
                          {ban.isLifted && ban.liftedAtUtc && (
                            <span>
                              Lifted: <FormattedTimestamp timestamp={ban.liftedAtUtc} />
                            </span>
                          )}
                        </div>
                      </div>
                    </div>

                    {isAuthenticated && ban.isActive && (
                      <Tooltip content="Lift this ban">
                        <Button
                          variant="subtle"
                          size="sm"
                          onClick={() => setLiftBanConfirm(ban)}
                          disabled={liftingBan === ban.id}
                        >
                          {liftingBan === ban.id ? (
                            <Loader2 className="w-4 h-4 animate-spin" />
                          ) : (
                            <Shield className="w-4 h-4" />
                          )}
                        </Button>
                      </Tooltip>
                    )}
                  </div>
                </CardContent>
              </Card>
            ))}
          </div>
        )}
      </div>

      {/* Terminate All Confirmation Modal */}
      <Modal
        opened={terminateAllConfirm}
        onClose={() => !terminatingAll && setTerminateAllConfirm(false)}
        title={
          <div className="flex items-center gap-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Terminate All Sessions</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            This will terminate <strong>{activeSessions.length}</strong> active prefill session(s).
            All Docker containers will be stopped and removed.
          </p>

          <Alert color="yellow">
            <p className="text-sm">Users will need to restart their prefill sessions.</p>
          </Alert>

          <div className="flex justify-end gap-3 pt-2">
            <Button
              variant="default"
              onClick={() => setTerminateAllConfirm(false)}
              disabled={terminatingAll}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={handleTerminateAll}
              loading={terminatingAll}
            >
              Terminate All
            </Button>
          </div>
        </div>
      </Modal>

      {/* Ban Confirmation Modal */}
      <Modal
        opened={banConfirm !== null}
        onClose={() => !banningSession && setBanConfirm(null)}
        title={
          <div className="flex items-center gap-3">
            <Ban className="w-6 h-6 text-themed-error" />
            <span>Ban Steam User</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            This will ban the Steam user associated with this session.
            They will not be able to log in to prefill in the future.
          </p>

          <div>
            <label className="block text-sm font-medium text-themed-secondary mb-1">
              Reason (optional)
            </label>
            <input
              type="text"
              value={banConfirm?.reason || ''}
              onChange={(e) => banConfirm && setBanConfirm({ ...banConfirm, reason: e.target.value })}
              placeholder="Enter ban reason..."
              className="w-full px-3 py-2 rounded text-sm bg-themed-tertiary text-themed-primary border border-themed-primary"
            />
          </div>

          <Alert color="red">
            <p className="text-sm">
              The ban is based on the Steam username. The same user cannot create a new session.
            </p>
          </Alert>

          <div className="flex justify-end gap-3 pt-2">
            <Button
              variant="default"
              onClick={() => setBanConfirm(null)}
              disabled={banningSession !== null}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={handleBanBySession}
              loading={banningSession !== null}
            >
              Ban User
            </Button>
          </div>
        </div>
      </Modal>

      {/* Lift Ban Confirmation Modal */}
      <Modal
        opened={liftBanConfirm !== null}
        onClose={() => !liftingBan && setLiftBanConfirm(null)}
        title={
          <div className="flex items-center gap-3">
            <Shield className="w-6 h-6 text-themed-primary" />
            <span>Lift Ban</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to lift this ban? The user will be able to use prefill again.
          </p>

          {liftBanConfirm && (
            <div className="p-3 rounded bg-themed-tertiary">
              <div className="text-sm">
                <span className="font-mono text-themed-primary">
                  {liftBanConfirm.username || 'Unknown'}
                </span>
                {liftBanConfirm.banReason && (
                  <div className="mt-2 text-themed-muted">
                    Reason: {liftBanConfirm.banReason}
                  </div>
                )}
              </div>
            </div>
          )}

          <div className="flex justify-end gap-3 pt-2">
            <Button
              variant="default"
              onClick={() => setLiftBanConfirm(null)}
              disabled={liftingBan !== null}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              onClick={() => liftBanConfirm && handleLiftBan(liftBanConfirm.id)}
              loading={liftingBan !== null}
            >
              Lift Ban
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default PrefillSessionsSection;
