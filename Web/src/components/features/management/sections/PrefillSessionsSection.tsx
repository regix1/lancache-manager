import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
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
  XCircle,
  Activity
} from 'lucide-react';
import { Card, CardContent } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import { Pagination } from '@components/ui/Pagination';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Checkbox } from '@components/ui/Checkbox';
import { AccordionSection } from '@components/ui/AccordionSection';
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
import './PrefillSessionsSection.css';

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

// Prefill history status badge
const HistoryStatusBadge: React.FC<{ status: string; completedAtUtc?: string }> = ({ status, completedAtUtc }) => {
  const { t } = useTranslation();

  const getEffectiveStatus = () => {
    const normalizedStatus = status.toLowerCase();
    if (completedAtUtc && normalizedStatus === 'inprogress') {
      return 'completed';
    }
    return normalizedStatus;
  };

  const effectiveStatus = getEffectiveStatus();

  const getStatusConfig = () => {
    switch (effectiveStatus) {
      case 'completed':
        return { className: 'prefill-status-badge prefill-status-completed' };
      case 'inprogress':
        return { className: 'prefill-status-badge prefill-status-progress' };
      case 'failed':
      case 'error':
        return { className: 'prefill-status-badge prefill-status-failed' };
      case 'cancelled':
        return { className: 'prefill-status-badge prefill-status-cancelled' };
      case 'cached':
        return { className: 'prefill-status-badge prefill-status-cached' };
      default:
        return { className: 'prefill-status-badge prefill-status-default' };
    }
  };

  const config = getStatusConfig();

  const getDisplayStatus = () => {
    switch (effectiveStatus) {
      case 'completed': return t('management.prefillSessions.historyStatusBadges.completed');
      case 'inprogress': return t('management.prefillSessions.historyStatusBadges.inProgress');
      case 'failed': return t('management.prefillSessions.historyStatusBadges.failed');
      case 'error': return t('management.prefillSessions.historyStatusBadges.error');
      case 'cancelled': return t('management.prefillSessions.historyStatusBadges.cancelled');
      case 'cached': return t('management.prefillSessions.historyStatusBadges.cached');
      default: return status;
    }
  };

  return (
    <span className={config.className}>
      {getDisplayStatus()}
    </span>
  );
};

// Status badge component
const StatusBadge: React.FC<{ status: string; isLive?: boolean }> = ({ status, isLive }) => {
  const { t } = useTranslation();

  const getStatusClass = (status: string) => {
    switch (status.toLowerCase()) {
      case 'active':
      case 'authenticated':
        return 'prefill-session-badge prefill-session-active';
      case 'pendingauth':
      case 'awaitingcredential':
        return 'prefill-session-badge prefill-session-pending';
      case 'terminated':
      case 'expired':
        return 'prefill-session-badge prefill-session-terminated';
      case 'orphaned':
        return 'prefill-session-badge prefill-session-orphaned';
      case 'cleaned':
        return 'prefill-session-badge prefill-session-cleaned';
      default:
        return 'prefill-session-badge prefill-session-default';
    }
  };

  return (
    <div className="flex items-center gap-1.5 flex-wrap">
      <span className={getStatusClass(status)}>
        {status}
      </span>
      {isLive && (
        <Tooltip content={t('management.prefillSessions.tooltips.sessionActive')}>
          <span className="prefill-live-badge">
            <span className="prefill-live-indicator" />
            {t('management.prefillSessions.statusBadges.live')}
          </span>
        </Tooltip>
      )}
    </div>
  );
};

// Summary stat card component
const StatCard: React.FC<{
  icon: React.ReactNode;
  value: number;
  label: string;
  iconBgClass: string;
}> = ({ icon, value, label, iconBgClass }) => (
  <div className="prefill-stat-card">
    <div className={`prefill-stat-icon ${iconBgClass}`}>
      {icon}
    </div>
    <div className="prefill-stat-content">
      <div className="prefill-stat-value">{value}</div>
      <div className="prefill-stat-label">{label}</div>
    </div>
  </div>
);

// Session card component for both live and historical sessions
const SessionCard: React.FC<{
  session: DaemonSessionDto | PrefillSessionDto;
  isLive: boolean;
  isAuthenticated: boolean;
  historyData: PrefillHistoryEntryDto[];
  isHistoryExpanded: boolean;
  isLoadingHistory: boolean;
  onToggleHistory: () => void;
  onTerminate?: () => void;
  onBan?: () => void;
  isTerminating?: boolean;
  isBanning?: boolean;
  historyPage: number;
  onHistoryPageChange: (page: number) => void;
}> = ({
  session,
  isLive,
  isAuthenticated,
  historyData,
  isHistoryExpanded,
  isLoadingHistory,
  onToggleHistory,
  onTerminate,
  onBan,
  isTerminating,
  isBanning,
  historyPage,
  onHistoryPageChange
}) => {
  const { t } = useTranslation();
  const historyPageSize = 5;

  // Normalize session data between DaemonSessionDto and PrefillSessionDto
  const isDaemonSession = 'id' in session && !('sessionId' in session);
  const status = session.status;
  const isPrefilling = isDaemonSession ? (session as DaemonSessionDto).isPrefilling : false;
  const steamUsername = isDaemonSession ? (session as DaemonSessionDto).steamUsername : (session as PrefillSessionDto).steamUsername;
  const containerName = isDaemonSession ? (session as DaemonSessionDto).containerName : (session as PrefillSessionDto).containerName;
  const createdAt = isDaemonSession ? (session as DaemonSessionDto).createdAt : (session as PrefillSessionDto).createdAtUtc;
  const endedAt = isDaemonSession ? undefined : (session as PrefillSessionDto).endedAtUtc;
  const ipAddress = isDaemonSession ? (session as DaemonSessionDto).ipAddress : undefined;
  const operatingSystem = isDaemonSession ? (session as DaemonSessionDto).operatingSystem : undefined;
  const browser = isDaemonSession ? (session as DaemonSessionDto).browser : undefined;
  const currentAppName = isDaemonSession ? (session as DaemonSessionDto).currentAppName : undefined;
  const totalBytesTransferred = isDaemonSession ? (session as DaemonSessionDto).totalBytesTransferred : undefined;
  const isAuthenticated_ = isDaemonSession
    ? (session as DaemonSessionDto).authState === 'Authenticated'
    : (session as PrefillSessionDto).isAuthenticated;

  const totalBytesFromHistory = historyData
    ? historyData.reduce((sum, e) => sum + Math.max(e.bytesDownloaded, e.totalBytes || 0), 0)
    : 0;
  const gamesCount = historyData?.length || 0;

  const totalPages = historyData ? Math.ceil(historyData.length / historyPageSize) : 0;
  const startIdx = (historyPage - 1) * historyPageSize;
  const paginatedEntries = historyData?.slice(startIdx, startIdx + historyPageSize) || [];

  return (
    <Card className="prefill-session-card">
      <CardContent className="p-0">
        {/* Main session info */}
        <div className="prefill-session-content">
          {/* Left side: Status indicator and session info */}
          <div className="prefill-session-main">
            {/* Status indicator */}
            <div className={`prefill-session-indicator ${
              isPrefilling ? 'prefill-indicator-downloading' :
              isLive ? 'prefill-indicator-active' :
              status === 'Terminated' ? 'prefill-indicator-terminated' :
              'prefill-indicator-default'
            }`}>
              {isPrefilling ? (
                <Loader2 className="w-5 h-5 animate-spin" />
              ) : isLive ? (
                <Play className="w-5 h-5" />
              ) : status === 'Terminated' ? (
                <StopCircle className="w-5 h-5" />
              ) : (
                <Container className="w-5 h-5" />
              )}
            </div>

            {/* Session details */}
            <div className="prefill-session-details">
              {/* Header: Username and status */}
              <div className="prefill-session-header">
                {steamUsername ? (
                  <span className="prefill-session-username">
                    <User className="w-3.5 h-3.5" />
                    {steamUsername}
                  </span>
                ) : (
                  <span className="prefill-session-no-user">
                    {isAuthenticated_
                      ? t('management.prefillSessions.labels.unauthorizedAccount')
                      : t('management.prefillSessions.labels.notLoggedInSession')}
                  </span>
                )}
                <StatusBadge status={status} isLive={isLive} />
                {!isDaemonSession && (session as PrefillSessionDto).isAuthenticated && (
                  <Tooltip content={t('management.prefillSessions.tooltips.steamAuthenticated')}>
                    <CheckCircle className="w-4 h-4 icon-green flex-shrink-0" />
                  </Tooltip>
                )}
              </div>

              {/* Prefilling status */}
              {isPrefilling && (
                <div className="prefill-downloading-status">
                  <Activity className="w-4 h-4" />
                  <span className="prefill-downloading-name">
                    {currentAppName || t('management.prefillSessions.labels.loading')}
                  </span>
                  {(totalBytesTransferred ?? 0) > 0 && (
                    <span className="prefill-downloading-size">
                      {formatBytes(totalBytesTransferred!)}
                    </span>
                  )}
                </div>
              )}

              {/* Container name */}
              {containerName && (
                <div className="prefill-session-container">
                  <Container className="w-3 h-3 flex-shrink-0" />
                  <span className="font-mono truncate">{containerName}</span>
                </div>
              )}

              {/* Metadata row */}
              <div className="prefill-session-meta">
                <span className="prefill-meta-item">
                  <Clock className="w-3 h-3" />
                  <FormattedTimestamp timestamp={createdAt} />
                </span>
                {endedAt && (
                  <span className="prefill-meta-item">
                    → <FormattedTimestamp timestamp={endedAt} />
                  </span>
                )}
                {ipAddress && (
                  <span className="prefill-meta-item hidden sm:flex">
                    <Network className="w-3 h-3" />
                    <span className="font-mono">{cleanIpAddress(ipAddress)}</span>
                  </span>
                )}
                {(operatingSystem || browser) && (
                  <span className="prefill-meta-item hidden md:flex">
                    <Monitor className="w-3 h-3" />
                    {operatingSystem || browser}
                  </span>
                )}
              </div>
            </div>
          </div>

          {/* Right side: Stats and actions */}
          <div className="prefill-session-actions">
            {/* Stats badges */}
            {(gamesCount > 0 || totalBytesFromHistory > 0 || (!isPrefilling && (totalBytesTransferred ?? 0) > 0)) && (
              <div className="prefill-session-stats">
                {gamesCount > 0 && (
                  <Tooltip content={t('management.prefillSessions.tooltips.gamesPrefilled', { count: gamesCount })}>
                    <span className="prefill-stat-badge prefill-stat-games">
                      <Gamepad2 className="w-3.5 h-3.5" />
                      <span>{gamesCount}</span>
                    </span>
                  </Tooltip>
                )}
                {(totalBytesFromHistory > 0 || (!isPrefilling && (totalBytesTransferred ?? 0) > 0)) && (
                  <Tooltip content={t('management.prefillSessions.tooltips.totalDataDownloaded')}>
                    <span className="prefill-stat-badge prefill-stat-bytes">
                      {formatBytes(totalBytesFromHistory || totalBytesTransferred || 0)}
                    </span>
                  </Tooltip>
                )}
              </div>
            )}

            {/* Action buttons */}
            <div className="prefill-action-buttons">
              <Button
                variant="subtle"
                size="sm"
                onClick={onToggleHistory}
                className="prefill-expand-btn"
              >
                {isLoadingHistory ? (
                  <Loader2 className="w-4 h-4 animate-spin" />
                ) : isHistoryExpanded ? (
                  <ChevronUp className="w-4 h-4" />
                ) : (
                  <ChevronDown className="w-4 h-4" />
                )}
              </Button>

              {isAuthenticated && isLive && (
                <>
                  {steamUsername && onBan && (
                    <Tooltip content={t('management.prefillSessions.tooltips.banUser')}>
                      <Button
                        variant="subtle"
                        size="sm"
                        color="red"
                        onClick={onBan}
                        disabled={isBanning}
                      >
                        {isBanning ? (
                          <Loader2 className="w-4 h-4 animate-spin" />
                        ) : (
                          <Ban className="w-4 h-4" />
                        )}
                      </Button>
                    </Tooltip>
                  )}
                  {onTerminate && (
                    <Tooltip content={t('management.prefillSessions.tooltips.terminateSession')}>
                      <Button
                        variant="subtle"
                        size="sm"
                        color="red"
                        onClick={onTerminate}
                        disabled={isTerminating}
                      >
                        {isTerminating ? (
                          <Loader2 className="w-4 h-4 animate-spin" />
                        ) : (
                          <StopCircle className="w-4 h-4" />
                        )}
                      </Button>
                    </Tooltip>
                  )}
                </>
              )}
            </div>
          </div>
        </div>

        {/* Expandable history section */}
        {isHistoryExpanded && (
          <div className="prefill-history-section">
            <div className="prefill-history-header">
              <Gamepad2 className="w-4 h-4 text-themed-muted" />
              <span>{t('management.prefillSessions.labels.prefillHistory')}</span>
            </div>

            {isLoadingHistory ? (
              <div className="prefill-history-loading">
                <Loader2 className="w-4 h-4 animate-spin text-themed-muted" />
                <span>{t('management.prefillSessions.labels.loadingHistory')}</span>
              </div>
            ) : !historyData || historyData.length === 0 ? (
              <div className="prefill-history-empty">
                {isLive
                  ? t('management.prefillSessions.labels.noPrefillHistoryYet')
                  : t('management.prefillSessions.labels.noPrefillHistoryRecorded')}
              </div>
            ) : (
              <>
                {/* Summary stats */}
                <div className="prefill-history-summary">
                  <span>{t('management.prefillSessions.labels.gamesPrefilled', { count: historyData.length })}</span>
                  {totalBytesFromHistory > 0 && (
                    <span>{t('management.prefillSessions.labels.total', { bytes: formatBytes(totalBytesFromHistory) })}</span>
                  )}
                </div>

                {/* History entries */}
                <div className="prefill-history-list">
                  {paginatedEntries.map(entry => (
                    <div key={entry.id} className="prefill-history-entry">
                      <div className="prefill-history-entry-main">
                        <Gamepad2 className="w-4 h-4 text-themed-muted flex-shrink-0" />
                        <div className="prefill-history-entry-content">
                          <div className="prefill-history-entry-header">
                            <span className="prefill-history-entry-name">
                              {entry.appName || `App ${entry.appId}`}
                            </span>
                            <HistoryStatusBadge status={entry.status} completedAtUtc={entry.completedAtUtc} />
                          </div>
                          <div className="prefill-history-entry-meta">
                            <span>Started: <FormattedTimestamp timestamp={entry.startedAtUtc} /></span>
                            {entry.completedAtUtc && (
                              <span>Completed: <FormattedTimestamp timestamp={entry.completedAtUtc} /></span>
                            )}
                            {(entry.bytesDownloaded > 0 || entry.totalBytes > 0) && (
                              <span>
                                {entry.totalBytes > 0 && entry.bytesDownloaded !== entry.totalBytes && entry.status.toLowerCase() !== 'cached'
                                  ? `${formatBytes(entry.bytesDownloaded)} / ${formatBytes(entry.totalBytes)}`
                                  : formatBytes(entry.bytesDownloaded || entry.totalBytes)}
                              </span>
                            )}
                          </div>
                          {entry.errorMessage && (
                            <div className="prefill-history-entry-error">
                              <XCircle className="w-3 h-3" />
                              <span>{entry.errorMessage}</span>
                            </div>
                          )}
                        </div>
                      </div>
                    </div>
                  ))}
                </div>

                {/* Pagination */}
                {totalPages > 1 && (
                  <div className="prefill-history-pagination">
                    <Pagination
                      currentPage={historyPage}
                      totalPages={totalPages}
                      totalItems={historyData.length}
                      itemsPerPage={historyPageSize}
                      onPageChange={onHistoryPageChange}
                      itemLabel={t('management.prefillSessions.labels.games')}
                      compact
                    />
                  </div>
                )}
              </>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  );
};

// Banned user card component
const BannedUserCard: React.FC<{
  ban: BannedSteamUserDto;
  isAuthenticated: boolean;
  onLiftBan: () => void;
  isLifting: boolean;
}> = ({ ban, isAuthenticated, onLiftBan, isLifting }) => {
  const { t } = useTranslation();

  return (
    <div className="prefill-ban-card">
      <div className={`prefill-ban-icon ${ban.isActive ? 'prefill-ban-active' : 'prefill-ban-lifted'}`}>
        <Ban className="w-4 h-4" />
      </div>
      <div className="prefill-ban-content">
        <div className="prefill-ban-header">
          <span className="prefill-ban-username">
            {ban.username || t('management.prefillSessions.bannedUsers.unknown')}
          </span>
          <span className={`prefill-ban-status ${ban.isActive ? 'active' : 'lifted'}`}>
            {ban.isActive
              ? t('management.prefillSessions.bannedUsers.active')
              : t('management.prefillSessions.bannedUsers.lifted')}
          </span>
        </div>
        <div className="prefill-ban-meta">
          <span>
            {t('management.prefillSessions.bannedUsers.banned', { time: '' })}
            <FormattedTimestamp timestamp={ban.bannedAtUtc} />
          </span>
          {ban.banReason && (
            <span className="prefill-ban-reason">
              {t('management.prefillSessions.bannedUsers.reason', { reason: ban.banReason })}
            </span>
          )}
          {ban.expiresAtUtc && (
            <span>
              {t('management.prefillSessions.bannedUsers.expires', { time: '' })}
              <FormattedTimestamp timestamp={ban.expiresAtUtc} />
            </span>
          )}
          {ban.isLifted && ban.liftedAtUtc && (
            <span>
              {t('management.prefillSessions.bannedUsers.liftedAt', { time: '' })}
              <FormattedTimestamp timestamp={ban.liftedAtUtc} />
            </span>
          )}
        </div>
      </div>
      {isAuthenticated && ban.isActive && (
        <Tooltip content={t('management.prefillSessions.tooltips.liftBan')}>
          <Button
            variant="subtle"
            size="sm"
            onClick={onLiftBan}
            disabled={isLifting}
            className="prefill-ban-action"
          >
            {isLifting ? (
              <Loader2 className="w-4 h-4 animate-spin" />
            ) : (
              <Shield className="w-4 h-4" />
            )}
          </Button>
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
  const { t } = useTranslation();
  const { on, off } = useSignalR();

  // Accordion states
  const [liveSessionsExpanded, setLiveSessionsExpanded] = useState(true);
  const [historyExpanded, setHistoryExpanded] = useState(true);
  const [bansExpanded, setBansExpanded] = useState(true);

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

  // Load sessions and pre-fetch history
  const loadSessions = useCallback(async () => {
    setLoadingSessions(true);
    try {
      const [sessionsRes, activeRes] = await Promise.all([
        ApiService.getPrefillSessions(page, pageSize, statusFilter || undefined),
        ApiService.getActivePrefillSessions()
      ]);
      setSessions(sessionsRes.sessions);
      setTotalCount(sessionsRes.totalCount);
      setActiveSessions(activeRes);

      // Pre-fetch history for all sessions
      const historyPromises = sessionsRes.sessions.map(async (session) => {
        try {
          const history = await ApiService.getPrefillSessionHistory(session.sessionId);
          return { sessionId: session.sessionId, history };
        } catch {
          return { sessionId: session.sessionId, history: [] };
        }
      });

      const activeHistoryPromises = activeRes.map(async (session) => {
        try {
          const history = await ApiService.getPrefillSessionHistory(session.id);
          return { sessionId: session.id, history };
        } catch {
          return { sessionId: session.id, history: [] };
        }
      });

      Promise.all([...historyPromises, ...activeHistoryPromises]).then((results) => {
        const newHistoryData: Record<string, PrefillHistoryEntryDto[]> = {};
        results.forEach(({ sessionId, history }) => {
          newHistoryData[sessionId] = history;
        });
        setHistoryData(prev => ({ ...prev, ...newHistoryData }));
      });
    } catch (error) {
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
    setLoadingHistory(prev => new Set(prev).add(sessionId));
    try {
      const history = await ApiService.getPrefillSessionHistory(sessionId);
      setHistoryData(prev => ({ ...prev, [sessionId]: history }));
    } catch (error) {
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

  // SignalR subscriptions
  useEffect(() => {
    const handleSessionCreated = (session: DaemonSessionCreatedEvent) => {
      setActiveSessions(prev => {
        if (prev.some(s => s.id === session.id)) return prev;
        return [...prev, session as DaemonSessionDto];
      });
    };

    const handleSessionUpdated = (session: DaemonSessionUpdatedEvent) => {
      setActiveSessions(prev =>
        prev.map(s => s.id === session.id ? session as DaemonSessionDto : s)
      );
    };

    const handleSessionTerminated = async (event: DaemonSessionTerminatedEvent) => {
      setActiveSessions(prev => prev.filter(s => s.id !== event.sessionId));
      try {
        const [sessionsRes, activeRes] = await Promise.all([
          ApiService.getPrefillSessions(1, 20),
          ApiService.getActivePrefillSessions()
        ]);
        setSessions(sessionsRes.sessions);
        setTotalCount(sessionsRes.totalCount);
        setActiveSessions(activeRes);
      } catch {
        // Ignore errors in SignalR handler
      }
    };

    const handlePrefillHistoryUpdated = async (event: PrefillHistoryUpdatedEvent) => {
      try {
        const history = await ApiService.getPrefillSessionHistory(event.sessionId);
        setHistoryData(prev => ({ ...prev, [event.sessionId]: history }));
      } catch {
        // Ignore errors in SignalR handler
      }
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
  }, [on, off]);

  // Action handlers
  const handleTerminateSession = async (sessionId: string) => {
    setTerminatingSession(sessionId);
    try {
      await ApiService.terminatePrefillSession(sessionId, 'Terminated by admin');
      onSuccess(t('management.prefillSessions.actions.terminateSession'));
      await loadSessions();
    } catch (error) {
      onError(getErrorMessage(error));
    } finally {
      setTerminatingSession(null);
    }
  };

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

  const handleBanBySession = async () => {
    if (!banConfirm) return;
    setBanningSession(banConfirm.sessionId);
    try {
      await ApiService.banSteamUserBySession(banConfirm.sessionId, banConfirm.reason || undefined);
      onSuccess(t('management.prefillSessions.actions.banUser'));
      setBanConfirm(null);
      await Promise.all([loadSessions(), loadBans()]);
    } catch (error) {
      onError(getErrorMessage(error));
    } finally {
      setBanningSession(null);
    }
  };

  const handleLiftBan = async (banId: number) => {
    setLiftingBan(banId);
    try {
      await ApiService.liftSteamBan(banId);
      onSuccess(t('management.prefillSessions.actions.liftBan'));
      setLiftBanConfirm(null);
      await loadBans();
    } catch (error) {
      onError(getErrorMessage(error));
    } finally {
      setLiftingBan(null);
    }
  };

  const totalPages = Math.ceil(totalCount / pageSize);
  const activeBansCount = bans.filter(b => b.isActive).length;

  return (
    <div
      className="management-section prefill-sessions-section animate-fade-in"
      role="tabpanel"
      id="panel-prefill-sessions"
      aria-labelledby="tab-prefill-sessions"
    >
      {/* Section Header */}
      <div className="prefill-section-header">
        <div className="prefill-section-title">
          <h2>{t('management.prefillSessions.title')}</h2>
          <p>{t('management.prefillSessions.subtitle')}</p>
        </div>
        <div className="prefill-header-actions">
          <Button
            variant="subtle"
            size="sm"
            onClick={() => { loadSessions(); loadBans(); }}
            disabled={loadingSessions || loadingBans}
            className="prefill-refresh-btn"
          >
            <RefreshCw className={`w-4 h-4 ${loadingSessions || loadingBans ? 'animate-spin' : ''}`} />
            <span className="hidden sm:inline">{t('common.refresh')}</span>
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
              <span className="hidden sm:inline">
                {t('management.prefillSessions.endAll', { count: activeSessions.length })}
              </span>
              <span className="sm:hidden">{activeSessions.length}</span>
            </Button>
          )}
        </div>
      </div>

      {/* Summary Stats */}
      <div className="prefill-stats-grid">
        <StatCard
          icon={<Play className="w-5 h-5 icon-green" />}
          value={activeSessions.length}
          label={t('management.prefillSessions.activeSessions')}
          iconBgClass="icon-bg-green"
        />
        <StatCard
          icon={<Container className="w-5 h-5 icon-primary" />}
          value={totalCount}
          label={t('management.prefillSessions.totalSessions')}
          iconBgClass="icon-bg-blue"
        />
        <StatCard
          icon={<Ban className="w-5 h-5 icon-red" />}
          value={activeBansCount}
          label={t('management.prefillSessions.activeBans')}
          iconBgClass="icon-bg-red"
        />
      </div>

      {/* Live Sessions Accordion */}
      <AccordionSection
        title={t('management.prefillSessions.liveSessions', { count: activeSessions.length })}
        count={activeSessions.length}
        icon={Play}
        iconColor="var(--theme-icon-green)"
        isExpanded={liveSessionsExpanded}
        onToggle={() => setLiveSessionsExpanded(!liveSessionsExpanded)}
      >
        {loadingSessions ? (
          <div className="prefill-loading-state">
            <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
            <span>{t('management.prefillSessions.loadingSessions')}</span>
          </div>
        ) : activeSessions.length === 0 ? (
          <div className="prefill-empty-state">
            <Container className="w-12 h-12 opacity-50" />
            <p className="prefill-empty-title">{t('management.prefillSessions.noActiveSessions')}</p>
            <p className="prefill-empty-desc">{t('management.prefillSessions.noActiveSessionsDesc')}</p>
          </div>
        ) : (
          <div className="prefill-sessions-list">
            {activeSessions.map(session => (
              <SessionCard
                key={session.id}
                session={session}
                isLive={true}
                isAuthenticated={isAuthenticated}
                historyData={historyData[session.id] || []}
                isHistoryExpanded={expandedHistory.has(session.id)}
                isLoadingHistory={loadingHistory.has(session.id)}
                onToggleHistory={() => toggleHistory(session.id)}
                onTerminate={() => handleTerminateSession(session.id)}
                onBan={session.steamUsername ? () => setBanConfirm({ sessionId: session.id, reason: '' }) : undefined}
                isTerminating={terminatingSession === session.id}
                isBanning={banningSession === session.id}
                historyPage={historyPage[session.id] || 1}
                onHistoryPageChange={(p) => setHistoryPage(prev => ({ ...prev, [session.id]: p }))}
              />
            ))}
          </div>
        )}
      </AccordionSection>

      {/* Session History Accordion */}
      <AccordionSection
        title={t('management.prefillSessions.sessionHistory', { count: totalCount })}
        count={totalCount}
        icon={Clock}
        iconColor="var(--theme-primary)"
        isExpanded={historyExpanded}
        onToggle={() => setHistoryExpanded(!historyExpanded)}
        badge={
          <div className="prefill-filter-inline">
            <EnhancedDropdown
              options={[
                { value: '', label: t('management.prefillSessions.statusFilters.all') },
                { value: 'Active', label: t('management.prefillSessions.statusFilters.active') },
                { value: 'Terminated', label: t('management.prefillSessions.statusFilters.terminated') },
                { value: 'Orphaned', label: t('management.prefillSessions.statusFilters.orphaned') },
                { value: 'Cleaned', label: t('management.prefillSessions.statusFilters.cleaned') }
              ] as DropdownOption[]}
              value={statusFilter}
              onChange={(value) => { setStatusFilter(value); setPage(1); }}
              placeholder={t('management.prefillSessions.statusFilters.all')}
              compactMode
              className="min-w-[120px]"
              dropdownWidth="140px"
            />
          </div>
        }
      >
        {loadingSessions ? (
          <div className="prefill-loading-state">
            <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
            <span>{t('management.prefillSessions.loading')}</span>
          </div>
        ) : sessions.length === 0 ? (
          <div className="prefill-empty-state">
            <p>{t('management.prefillSessions.noSessionsFound')}</p>
          </div>
        ) : (
          <>
            <div className="prefill-sessions-list">
              {sessions.map(session => (
                <SessionCard
                  key={session.id}
                  session={session}
                  isLive={session.isLive}
                  isAuthenticated={isAuthenticated}
                  historyData={historyData[session.sessionId] || []}
                  isHistoryExpanded={expandedHistory.has(session.sessionId)}
                  isLoadingHistory={loadingHistory.has(session.sessionId)}
                  onToggleHistory={() => toggleHistory(session.sessionId)}
                  onTerminate={session.isLive ? () => handleTerminateSession(session.sessionId) : undefined}
                  onBan={session.isLive && session.steamUsername ? () => setBanConfirm({ sessionId: session.sessionId, reason: '' }) : undefined}
                  isTerminating={terminatingSession === session.sessionId}
                  isBanning={banningSession === session.sessionId}
                  historyPage={historyPage[session.sessionId] || 1}
                  onHistoryPageChange={(p) => setHistoryPage(prev => ({ ...prev, [session.sessionId]: p }))}
                />
              ))}
            </div>

            {totalPages > 1 && (
              <div className="prefill-pagination">
                <Pagination
                  currentPage={page}
                  totalPages={totalPages}
                  totalItems={totalCount}
                  itemsPerPage={pageSize}
                  onPageChange={setPage}
                  itemLabel={t('management.prefillSessions.labels.sessions', 'sessions')}
                />
              </div>
            )}
          </>
        )}
      </AccordionSection>

      {/* Banned Users Accordion */}
      <AccordionSection
        title={t('management.prefillSessions.bannedUsers.title', { count: activeBansCount })}
        count={activeBansCount}
        icon={Ban}
        iconColor="var(--theme-icon-red)"
        isExpanded={bansExpanded}
        onToggle={() => setBansExpanded(!bansExpanded)}
        badge={
          <Checkbox
            label={t('management.prefillSessions.bannedUsers.showLifted')}
            checked={includeLifted}
            onChange={(e) => setIncludeLifted(e.target.checked)}
          />
        }
      >
        {loadingBans ? (
          <div className="prefill-loading-state">
            <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
            <span>{t('management.prefillSessions.bannedUsers.loadingBans')}</span>
          </div>
        ) : bans.length === 0 ? (
          <div className="prefill-empty-state">
            <Shield className="w-12 h-12 opacity-50" />
            <p className="prefill-empty-title">{t('management.prefillSessions.bannedUsers.noBannedUsers')}</p>
            <p className="prefill-empty-desc">{t('management.prefillSessions.bannedUsers.noBannedUsersDesc')}</p>
          </div>
        ) : (
          <div className="prefill-bans-list">
            {bans.map(ban => (
              <BannedUserCard
                key={ban.id}
                ban={ban}
                isAuthenticated={isAuthenticated}
                onLiftBan={() => setLiftBanConfirm(ban)}
                isLifting={liftingBan === ban.id}
              />
            ))}
          </div>
        )}
      </AccordionSection>

      {/* Terminate All Confirmation Modal */}
      <Modal
        opened={terminateAllConfirm}
        onClose={() => !terminatingAll && setTerminateAllConfirm(false)}
        title={
          <div className="flex items-center gap-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.prefillSessions.modals.terminateAll.title')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.prefillSessions.modals.terminateAll.message', { count: activeSessions.length })}
          </p>
          <Alert color="yellow">
            <p className="text-sm">{t('management.prefillSessions.modals.terminateAll.warning')}</p>
          </Alert>
          <div className="flex flex-col-reverse sm:flex-row justify-end gap-3 pt-2">
            <Button
              variant="default"
              onClick={() => setTerminateAllConfirm(false)}
              disabled={terminatingAll}
              className="w-full sm:w-auto"
            >
              {t('management.prefillSessions.modals.terminateAll.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={handleTerminateAll}
              loading={terminatingAll}
              className="w-full sm:w-auto"
            >
              {t('management.prefillSessions.modals.terminateAll.confirm')}
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
            <span>{t('management.prefillSessions.modals.ban.title')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.prefillSessions.modals.ban.message')}
          </p>
          <div>
            <label className="block text-sm font-medium text-themed-secondary mb-1">
              {t('management.prefillSessions.modals.ban.reasonLabel')}
            </label>
            <input
              type="text"
              value={banConfirm?.reason || ''}
              onChange={(e) => banConfirm && setBanConfirm({ ...banConfirm, reason: e.target.value })}
              placeholder={t('management.prefillSessions.modals.ban.reasonPlaceholder')}
              className="prefill-input"
            />
          </div>
          <Alert color="red">
            <p className="text-sm">{t('management.prefillSessions.modals.ban.warning')}</p>
          </Alert>
          <div className="flex flex-col-reverse sm:flex-row justify-end gap-3 pt-2">
            <Button
              variant="default"
              onClick={() => setBanConfirm(null)}
              disabled={banningSession !== null}
              className="w-full sm:w-auto"
            >
              {t('management.prefillSessions.modals.ban.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={handleBanBySession}
              loading={banningSession !== null}
              className="w-full sm:w-auto"
            >
              {t('management.prefillSessions.modals.ban.confirm')}
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
            <span>{t('management.prefillSessions.modals.liftBan.title')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.prefillSessions.modals.liftBan.message')}
          </p>
          {liftBanConfirm && (
            <div className="p-3 rounded-lg bg-themed-tertiary">
              <div className="text-sm">
                <span className="font-mono text-themed-primary">
                  {liftBanConfirm.username || t('management.prefillSessions.bannedUsers.unknown')}
                </span>
                {liftBanConfirm.banReason && (
                  <div className="mt-2 text-themed-muted">
                    {t('management.prefillSessions.bannedUsers.reason', { reason: liftBanConfirm.banReason })}
                  </div>
                )}
              </div>
            </div>
          )}
          <div className="flex flex-col-reverse sm:flex-row justify-end gap-3 pt-2">
            <Button
              variant="default"
              onClick={() => setLiftBanConfirm(null)}
              disabled={liftingBan !== null}
              className="w-full sm:w-auto"
            >
              {t('management.prefillSessions.modals.liftBan.cancel')}
            </Button>
            <Button
              variant="filled"
              onClick={() => liftBanConfirm && handleLiftBan(liftBanConfirm.id)}
              loading={liftingBan !== null}
              className="w-full sm:w-auto"
            >
              {t('management.prefillSessions.modals.liftBan.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default PrefillSessionsSection;
