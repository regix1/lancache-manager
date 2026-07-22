import React, { useState, useEffect, useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Container,
  Play,
  StopCircle,
  Ban,
  MoreVertical,
  Shield,
  AlertTriangle,
  Clock,
  User,
  ChevronDown,
  ChevronUp,
  Gamepad2,
  XCircle,
  Activity,
  Server,
  RefreshCw
} from 'lucide-react';
import { Card, CardContent } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import {
  ActionMenu,
  ActionMenuItem,
  ActionMenuDangerItem,
  ActionMenuDivider
} from '@components/ui/ActionMenu';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import { Pagination } from '@components/ui/Pagination';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Checkbox } from '@components/ui/Checkbox';
import { AccordionSection } from '@components/ui/AccordionSection';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import Badge from '@components/ui/Badge';
import ApiService, {
  type PrefillSessionDto,
  type DaemonSessionDto,
  type BannedSteamUserDto,
  type PrefillHistoryEntryDto
} from '@services/api.service';
import type { PrefillSessionStatus } from '@/types/operations';
import { GAME_SERVICES, type GameServiceId } from '@/types/gameService';
import { getErrorMessage } from '@utils/error';
import { formatBytes } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { usePaginatedList } from '@hooks/usePaginatedList';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { cleanIpAddress } from '@components/features/user/types';
import LoadingSpinner from '@components/common/LoadingSpinner';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import { usePersistentPrefillContainerSignalR } from '@components/features/management/schedules/scheduled-prefill/usePersistentPrefillContainerSignalR';
import type {
  DaemonSessionCreatedEvent,
  DaemonSessionUpdatedEvent,
  DaemonSessionTerminatedEvent,
  PrefillHistoryUpdatedEvent,
  EpicPrefillHistoryUpdatedEvent
} from '@contexts/SignalRContext/types';
import './PrefillSessionsSection.css';

interface PrefillSessionsSectionProps {
  isAdmin: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
}

// Helper component for formatted timestamps
const FormattedTimestamp: React.FC<{ timestamp: string | undefined }> = ({ timestamp }) => {
  const formattedTime = useFormattedDateTime(timestamp);
  return <>{formattedTime}</>;
};

// Prefill history status badge
const HistoryStatusBadge: React.FC<{ status: string; completedAtUtc?: string }> = ({
  status,
  completedAtUtc
}) => {
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
      case 'completed':
        return t('management.prefillSessions.historyStatusBadges.completed');
      case 'inprogress':
        return t('management.prefillSessions.historyStatusBadges.inProgress');
      case 'failed':
        return t('management.prefillSessions.historyStatusBadges.failed');
      case 'error':
        return t('management.prefillSessions.historyStatusBadges.error');
      case 'cancelled':
        return t('management.prefillSessions.historyStatusBadges.cancelled');
      case 'cached':
        return t('management.prefillSessions.historyStatusBadges.cached');
      default:
        return status;
    }
  };

  return <span className={config.className}>{getDisplayStatus()}</span>;
};

// Resolve a session's raw platform string (e.g. "Steam", "Epic", "battlenet")
// to a strongly-typed GameServiceId. Defaults to Steam for legacy/unknown values.
const resolveServiceId = (platform: string): GameServiceId => {
  switch (platform.toLowerCase()) {
    case 'epic':
      return 'epic';
    case 'battlenet':
    case 'blizzard':
      return 'battlenet';
    case 'riot':
    case 'riotgames':
      return 'riot';
    case 'xbox':
      return 'xbox';
    case 'steam':
    default:
      return 'steam';
  }
};

// Friendly display name for the platform badge ("Steam" / "Epic Games" / "Battle.net").
const serviceDisplayName = (serviceId: GameServiceId): string =>
  GAME_SERVICES.find((service) => service.id === serviceId)?.name ?? serviceId;

// Battle.net and Riot are anonymous prefill services (no account login).
const isAnonymousServiceId = (serviceId: GameServiceId): boolean =>
  serviceId === 'battlenet' || serviceId === 'riot';

// Session/container lifecycle status -> single Badge variant + i18n key. One shared map
// so the session-card status pill and the persistent-container status text agree on tone.
const STATUS_BADGE_KEY: Record<string, string> = {
  active: 'active',
  authenticated: 'authenticated',
  pendingauth: 'pendingAuth',
  awaitingcredential: 'awaitingCredential',
  terminated: 'terminated',
  expired: 'expired',
  orphaned: 'orphaned',
  cleaned: 'cleaned',
  cancelled: 'cancelled',
  error: 'error'
};

const STATUS_BADGE_VARIANT: Record<string, 'success' | 'warning' | 'error' | 'neutral'> = {
  active: 'success',
  authenticated: 'success',
  pendingauth: 'warning',
  awaitingcredential: 'warning',
  terminated: 'error',
  expired: 'error',
  orphaned: 'neutral',
  cleaned: 'neutral',
  cancelled: 'neutral',
  error: 'error'
};

// Locator source values are opaque backend strings; unknown values fall back to the "unknown"
// label instead of throwing. dockerInspect and envFile share one label since both mean the value
// came from the lancache-dns container's settings.
const CACHE_ROUTE_SOURCE_KEY: Record<string, string> = {
  config: 'config',
  dns: 'dns',
  dockerInspect: 'lancacheDns',
  envFile: 'lancacheDns',
  detected: 'detected'
};

const getStatusBadgeVariant = (status: string): 'success' | 'warning' | 'error' | 'neutral' =>
  STATUS_BADGE_VARIANT[status.toLowerCase()] ?? 'neutral';

const getStatusBadgeLabelKey = (status: string): string | null =>
  STATUS_BADGE_KEY[status.toLowerCase()] ?? null;

// Status badge component — ONE Badge carries the session's lifecycle state; a small
// tone dot (not a second pill) marks a session as currently live in memory, since that
// distinction only ever needs to be visible in Session History (Live Sessions is always live).
const StatusBadge: React.FC<{ status: string; isLive?: boolean }> = ({ status, isLive }) => {
  const { t } = useTranslation();
  const labelKey = getStatusBadgeLabelKey(status);
  const label = labelKey ? t(`management.prefillSessions.statusBadges.${labelKey}`) : status;

  return (
    <div className="prefill-status-line">
      {isLive && (
        <Tooltip content={t('management.prefillSessions.tooltips.sessionActive')}>
          <span className="status-dot active prefill-status-live-dot" aria-hidden="true" />
        </Tooltip>
      )}
      <Badge variant={getStatusBadgeVariant(status)}>{label}</Badge>
      {isLive && (
        <span className="sr-only">{t('management.prefillSessions.statusBadges.live')}</span>
      )}
    </div>
  );
};

// Summary stat card component
const StatCard: React.FC<{
  icon: React.ReactNode;
  value: number | null | undefined;
  label: string;
  iconBgClass: string;
}> = ({ icon, value, label, iconBgClass }) => (
  <div className="well-surface prefill-stat-card">
    <div className={`icon-box icon-box--md prefill-stat-icon ${iconBgClass}`}>{icon}</div>
    <div className="prefill-stat-content">
      <div className="prefill-stat-value">{value == null ? '—' : value}</div>
      <div className="prefill-stat-label">{label}</div>
    </div>
  </div>
);

// In-view error + retry block, shared by the Live Sessions / Session History / Banned Users views
const PrefillErrorBlock: React.FC<{
  title: string;
  message: string;
  retryLabel: string;
  onRetry: () => void;
}> = ({ title, message, retryLabel, onRetry }) => (
  <div className="prefill-error-state">
    <Alert color="red" title={title}>
      <p className="text-sm">{message}</p>
    </Alert>
    <div className="prefill-error-retry">
      <Button variant="filled" color="gray" size="md" onClick={onRetry}>
        {retryLabel}
      </Button>
    </div>
  </div>
);

// Session card component for both live and historical sessions
const SessionCard: React.FC<{
  session: DaemonSessionDto | PrefillSessionDto;
  isLive: boolean;
  isAdmin: boolean;
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
  isAdmin,
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
  const [menuOpen, setMenuOpen] = useState(false);
  const historyPageSize = 5;

  // Normalize session data between DaemonSessionDto and PrefillSessionDto
  const isDaemonSession = 'id' in session && !('sessionId' in session);
  const status = session.status;
  const isPrefilling = isDaemonSession ? (session as DaemonSessionDto).isPrefilling : false;

  const platform = isDaemonSession
    ? (session as DaemonSessionDto).platform || 'Steam'
    : (session as PrefillSessionDto).platform || 'Steam';
  const serviceId = resolveServiceId(platform);
  const isAnonymousService = isAnonymousServiceId(serviceId);
  const platformDisplayName = serviceDisplayName(serviceId);
  const isPersistentSession = isDaemonSession
    ? ((session as DaemonSessionDto).isPersistent ?? false)
    : ((session as PrefillSessionDto).isPersistent ?? false);

  const displayUsername = isDaemonSession
    ? (session as DaemonSessionDto).username || (session as DaemonSessionDto).steamUsername
    : (session as PrefillSessionDto).username || (session as PrefillSessionDto).steamUsername;
  const containerName = isDaemonSession
    ? (session as DaemonSessionDto).containerName
    : (session as PrefillSessionDto).containerName;
  const createdAt = isDaemonSession
    ? (session as DaemonSessionDto).createdAt
    : (session as PrefillSessionDto).createdAtUtc;
  const endedAt = isDaemonSession ? undefined : (session as PrefillSessionDto).endedAtUtc;
  const ipAddress = isDaemonSession ? (session as DaemonSessionDto).ipAddress : undefined;
  const operatingSystem = isDaemonSession
    ? (session as DaemonSessionDto).operatingSystem
    : undefined;
  const browser = isDaemonSession ? (session as DaemonSessionDto).browser : undefined;
  const currentAppName = isDaemonSession ? (session as DaemonSessionDto).currentAppName : undefined;
  const totalBytesTransferred = isDaemonSession
    ? (session as DaemonSessionDto).totalBytesTransferred
    : undefined;
  const isAuthenticated_ = isDaemonSession
    ? (session as DaemonSessionDto).authState === 'Authenticated'
    : (session as PrefillSessionDto).isAuthenticated;

  const totalBytesFromHistory = historyData
    ? historyData.reduce((sum, e) => sum + Math.max(e.bytesDownloaded, e.totalBytes || 0), 0)
    : 0;
  const gamesCount = historyData?.length || 0;

  const { paginatedItems: paginatedEntries, totalPages } = usePaginatedList<PrefillHistoryEntryDto>(
    {
      items: historyData,
      pageSize: historyPageSize,
      page: historyPage,
      onPageChange: onHistoryPageChange
    }
  );

  return (
    <Card className="prefill-session-card">
      <CardContent className="p-0">
        {/* Main session info */}
        <div className="prefill-session-content">
          {/* Left side: Status indicator and session info */}
          <div className="prefill-session-main">
            {/* Status indicator */}
            <div
              className={`icon-box icon-box--md prefill-session-indicator ${
                isPrefilling
                  ? 'prefill-indicator-downloading'
                  : isLive
                    ? 'prefill-indicator-active'
                    : status === 'Terminated'
                      ? 'prefill-indicator-terminated'
                      : 'prefill-indicator-default'
              }`}
            >
              {isPrefilling ? (
                <LoadingSpinner inline size="md" />
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
                {displayUsername ? (
                  <span className={`prefill-session-username platform-${serviceId}`}>
                    <User className="w-3.5 h-3.5" />
                    {displayUsername}
                  </span>
                ) : (
                  <span className="prefill-session-no-user">
                    {isAnonymousService
                      ? t('management.prefillSessions.labels.anonymousAccount')
                      : isPersistentSession
                        ? t('management.prefillSessions.labels.persistentContainer')
                        : isAuthenticated_
                          ? t('management.prefillSessions.labels.unauthorizedAccount')
                          : t('management.prefillSessions.labels.notLoggedInSession')}
                  </span>
                )}
                <StatusBadge status={status} isLive={isLive} />
                <Badge variant="neutral">{platformDisplayName}</Badge>
                {isPersistentSession && (
                  <Badge variant="neutral">
                    {t('management.prefillSessions.labels.persistentBadge')}
                  </Badge>
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
                    <span className="prefill-downloading-size tabular-nums">
                      {formatBytes(totalBytesTransferred!)}
                    </span>
                  )}
                </div>
              )}

              {/* Session stats — games prefilled + data transferred (compact readouts) */}
              {(gamesCount > 0 ||
                totalBytesFromHistory > 0 ||
                (!isPrefilling && (totalBytesTransferred ?? 0) > 0)) && (
                <div className="prefill-stat-line">
                  {gamesCount > 0 && (
                    <Tooltip
                      content={t('management.prefillSessions.tooltips.gamesPrefilled', {
                        count: gamesCount
                      })}
                    >
                      <span className="prefill-stat-item">
                        <Gamepad2 className="w-3.5 h-3.5" />
                        <span className="tabular-nums">{gamesCount}</span>
                      </span>
                    </Tooltip>
                  )}
                  {(totalBytesFromHistory > 0 ||
                    (!isPrefilling && (totalBytesTransferred ?? 0) > 0)) && (
                    <Tooltip content={t('management.prefillSessions.tooltips.totalDataDownloaded')}>
                      <span className="prefill-stat-item tabular-nums">
                        {formatBytes(totalBytesFromHistory || totalBytesTransferred || 0)}
                      </span>
                    </Tooltip>
                  )}
                </div>
              )}

              {/* Metadata — single muted line, middot-separated */}
              <div className="prefill-session-meta">
                {containerName && (
                  <span className="prefill-meta-item font-mono">{containerName}</span>
                )}
                <span className="prefill-meta-item">
                  <FormattedTimestamp timestamp={createdAt} />
                </span>
                {endedAt && (
                  <span className="prefill-meta-item">
                    <FormattedTimestamp timestamp={endedAt} />
                  </span>
                )}
                {ipAddress && (
                  <span className="prefill-meta-item font-mono hidden sm:inline-flex">
                    {cleanIpAddress(ipAddress)}
                  </span>
                )}
                {(operatingSystem || browser) && (
                  <span className="prefill-meta-item hidden md:inline-flex">
                    {operatingSystem || browser}
                  </span>
                )}
              </div>
            </div>
          </div>

          {/* Right side: action buttons only (stats moved into the session details) */}
          <div className="prefill-session-actions">
            {/* Action buttons — destructive actions first, expand/collapse chevron last (far right) */}
            <div className="prefill-action-buttons">
              {isAdmin && isLive && (onBan || onTerminate) && (
                <ActionMenu
                  isOpen={menuOpen}
                  onClose={() => setMenuOpen(false)}
                  align="right"
                  trigger={
                    <Button
                      variant="filled"
                      color="gray"
                      size="md"
                      onClick={() => setMenuOpen((prev) => !prev)}
                      aria-label={t('common.moreActions', 'More actions')}
                      className="btn-icon-square"
                    >
                      <MoreVertical className="w-4 h-4" />
                    </Button>
                  }
                >
                  {onBan && (
                    <ActionMenuDangerItem
                      onClick={() => {
                        setMenuOpen(false);
                        onBan();
                      }}
                      icon={<Ban className="w-4 h-4" />}
                      disabled={isBanning}
                    >
                      {t('management.prefillSessions.tooltips.banUser')}
                    </ActionMenuDangerItem>
                  )}
                  {onTerminate && (
                    <ActionMenuDangerItem
                      onClick={() => {
                        setMenuOpen(false);
                        onTerminate();
                      }}
                      icon={<StopCircle className="w-4 h-4" />}
                      disabled={isTerminating}
                    >
                      {t('management.prefillSessions.tooltips.terminateSession')}
                    </ActionMenuDangerItem>
                  )}
                </ActionMenu>
              )}

              <Button
                variant="filled"
                color="gray"
                size="md"
                onClick={onToggleHistory}
                className="prefill-expand-btn btn-icon-square"
              >
                {isLoadingHistory ? (
                  <LoadingSpinner inline size="sm" />
                ) : isHistoryExpanded ? (
                  <ChevronUp className="w-4 h-4" />
                ) : (
                  <ChevronDown className="w-4 h-4" />
                )}
              </Button>
            </div>
          </div>
        </div>

        {/* Expandable history section */}
        <CollapsibleRegion open={isHistoryExpanded} contentClassName="prefill-history-section">
          <div className="prefill-history-header">
            <Gamepad2 className="w-4 h-4 text-themed-muted" />
            <span>{t('management.prefillSessions.labels.prefillHistory')}</span>
          </div>

          {isLoadingHistory ? (
            <div className="prefill-history-loading">
              <LoadingSpinner inline size="sm" className="text-themed-muted" />
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
                <span>
                  {t('management.prefillSessions.labels.gamesPrefilled', {
                    count: historyData.length
                  })}
                </span>
                {totalBytesFromHistory > 0 && (
                  <span>
                    {t('management.prefillSessions.labels.total', {
                      bytes: formatBytes(totalBytesFromHistory)
                    })}
                  </span>
                )}
              </div>

              {/* History entries */}
              <div className="prefill-history-list">
                {paginatedEntries.map((entry) => (
                  <div key={entry.id} className="prefill-history-entry rounded">
                    <div className="prefill-history-entry-main">
                      <Gamepad2 className="w-4 h-4 text-themed-muted flex-shrink-0" />
                      <div className="prefill-history-entry-content">
                        <div className="prefill-history-entry-header">
                          <span className="prefill-history-entry-name">
                            {entry.appName || `App ${entry.appId}`}
                          </span>
                          <HistoryStatusBadge
                            status={entry.status}
                            completedAtUtc={entry.completedAtUtc}
                          />
                        </div>
                        <div className="prefill-history-entry-meta">
                          <span>
                            Started: <FormattedTimestamp timestamp={entry.startedAtUtc} />
                          </span>
                          {entry.completedAtUtc && (
                            <span>
                              Completed: <FormattedTimestamp timestamp={entry.completedAtUtc} />
                            </span>
                          )}
                          {(entry.bytesDownloaded > 0 || entry.totalBytes > 0) && (
                            <span>
                              {entry.totalBytes > 0 &&
                              entry.bytesDownloaded !== entry.totalBytes &&
                              entry.status.toLowerCase() !== 'cached'
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
        </CollapsibleRegion>
      </CardContent>
    </Card>
  );
};

// Banned user card component
const BannedUserCard: React.FC<{
  ban: BannedSteamUserDto;
  isAdmin: boolean;
  onLiftBan: () => void;
  isLifting: boolean;
}> = ({ ban, isAdmin, onLiftBan, isLifting }) => {
  const { t } = useTranslation();

  return (
    <div className="well-surface prefill-ban-card">
      <div
        className={`icon-box icon-box--md prefill-ban-icon ${ban.isActive ? 'prefill-ban-active' : 'prefill-ban-lifted'}`}
      >
        <Ban className="w-4 h-4" />
      </div>
      <div className="prefill-ban-content">
        <div className="prefill-ban-header">
          <span className="prefill-ban-username">
            {ban.username || t('management.prefillSessions.bannedUsers.unknown')}
          </span>
          <Badge variant={ban.isActive ? 'error' : 'neutral'} className="prefill-ban-badge">
            {ban.isActive
              ? t('management.prefillSessions.bannedUsers.active')
              : t('management.prefillSessions.bannedUsers.lifted')}
          </Badge>
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
      {isAdmin && ban.isActive && (
        <Tooltip content={t('management.prefillSessions.tooltips.liftBan')}>
          <Button
            variant="filled"
            color="gray"
            size="md"
            onClick={onLiftBan}
            disabled={isLifting}
            className="prefill-ban-action"
          >
            {isLifting ? <LoadingSpinner inline size="sm" /> : <Shield className="w-4 h-4" />}
          </Button>
        </Tooltip>
      )}
    </div>
  );
};

// Persistent container card — read-only monitoring, dot-row status idiom (tone dot + plain
// text, no pill wall) modeled on ScheduledPrefillPersistentCard's status line but split into
// two facts (running state, login state) since both need to be independently scannable here.
const PersistentContainerCard: React.FC<{ container: PersistentPrefillContainerDto }> = ({
  container
}) => {
  const { t } = useTranslation();
  const baseKey = 'management.prefillSessions.persistentSessions';
  const serviceId = resolveServiceId(container.service);
  const isAnonymous = isAnonymousServiceId(serviceId);
  const displayName = serviceDisplayName(serviceId);

  const runTone: 'idle' | 'running' = container.isRunning ? 'running' : 'idle';
  const runLabel = container.isRunning
    ? t(`${baseKey}.status.running`)
    : t(`${baseKey}.status.stopped`);

  const showLoginState = container.isRunning && !isAnonymous;
  const loginTone: 'active' | 'warning' = container.needsRelogin
    ? 'warning'
    : container.isAuthenticated
      ? 'active'
      : 'warning';
  const loginLabel = container.needsRelogin
    ? t(`${baseKey}.status.needsRelogin`)
    : container.isAuthenticated
      ? t(`${baseKey}.status.authenticated`)
      : t(`${baseKey}.status.notLoggedIn`);

  const isPrefilling = container.isRunning && (container.isPrefilling ?? false);

  return (
    <Card className="prefill-persistent-card">
      <CardContent className="prefill-persistent-card__body">
        <div className="prefill-persistent-card__header">
          <Badge variant="neutral">{displayName}</Badge>
        </div>

        <div className="prefill-persistent-card__status-row">
          <span
            className={`status-dot prefill-persistent-card__status-dot prefill-persistent-card__status-dot--${runTone}`}
            aria-hidden="true"
          />
          <span className="prefill-persistent-card__status-text">{runLabel}</span>
        </div>

        {showLoginState && (
          <div className="prefill-persistent-card__status-row">
            <span
              className={`status-dot prefill-persistent-card__status-dot prefill-persistent-card__status-dot--${loginTone}`}
              aria-hidden="true"
            />
            <span className="prefill-persistent-card__status-text">{loginLabel}</span>
          </div>
        )}

        {isPrefilling && (
          <p className="prefill-persistent-card__activity">
            {container.currentAppName
              ? t(`${baseKey}.prefilling`, { game: container.currentAppName })
              : t(`${baseKey}.prefillingGeneric`)}
            {(container.totalBytesTransferred ?? 0) > 0 && (
              <span className="tabular-nums">
                {' '}
                &middot; {formatBytes(container.totalBytesTransferred ?? 0)}
              </span>
            )}
          </p>
        )}

        {container.isRunning && !isAnonymous && container.daemonAuthExpiresAtUtc && (
          <div className="prefill-persistent-card__meta-item">
            <span className="caps-label prefill-persistent-card__meta-label">
              {t(`${baseKey}.tokenExpiresAt`)}
            </span>
            <span className="prefill-persistent-card__meta-value tabular-nums">
              <FormattedTimestamp timestamp={container.daemonAuthExpiresAtUtc} />
            </span>
          </div>
        )}

        <div className="prefill-persistent-card__container-name font-mono">
          {container.sessionId}
        </div>
      </CardContent>
    </Card>
  );
};

const PrefillSessionsSection: React.FC<PrefillSessionsSectionProps> = ({
  isAdmin,
  onError,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { on, off } = useSignalR();

  // Accordion states
  const [liveSessionsExpanded, setLiveSessionsExpanded] = useState(true);
  const [persistentExpanded, setPersistentExpanded] = useState(true);
  const [historyExpanded, setHistoryExpanded] = useState(true);
  const [bansExpanded, setBansExpanded] = useState(true);

  // Persistent containers state (system-owned, read-only monitoring)
  const [persistentContainers, setPersistentContainers] = useState<PersistentPrefillContainerDto[]>(
    []
  );
  const [loadingPersistent, setLoadingPersistent] = useState(true);
  const [persistentError, setPersistentError] = useState<string | null>(null);

  // Sessions state
  const [sessions, setSessions] = useState<PrefillSessionDto[]>([]);
  const [activeSessions, setActiveSessions] = useState<DaemonSessionDto[]>([]);
  const [loadingSessions, setLoadingSessions] = useState(true);
  const [page, setPage] = useState(1);
  const [pageSize] = useState(20);
  const [totalCount, setTotalCount] = useState(0);
  const [statusFilter, setStatusFilter] = useState<PrefillSessionStatus | ''>('');
  const [platformFilter, setPlatformFilter] = useState<string>('all');

  // Cache routing target injected into prefill daemon containers; null until the sessions
  // response has loaded once (the backend reports null values until the first container starts).
  const [cacheRoute, setCacheRoute] = useState<{ ip: string | null; source: string | null } | null>(
    null
  );

  // In-view load-error states (per data view; null = no error)
  const [sessionsError, setSessionsError] = useState<string | null>(null);
  const [bansError, setBansError] = useState<string | null>(null);

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
    setSessionsError(null);
    try {
      const [sessionsRes, activeRes] = await Promise.all([
        ApiService.getPrefillSessions(
          page,
          pageSize,
          statusFilter || undefined,
          platformFilter === 'all' ? undefined : platformFilter
        ),
        ApiService.getActivePrefillSessions()
      ]);
      setSessions(sessionsRes.sessions);
      setTotalCount(sessionsRes.totalCount);
      setActiveSessions(activeRes);
      setCacheRoute({
        ip: sessionsRes.lastPrefillCacheIp ?? null,
        source: sessionsRes.lastPrefillCacheIpSource ?? null
      });

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
        setHistoryData((prev) => ({ ...prev, ...newHistoryData }));
      });
    } catch (error) {
      setSessionsError(getErrorMessage(error));
      onError(getErrorMessage(error));
    } finally {
      setLoadingSessions(false);
    }
  }, [page, pageSize, statusFilter, platformFilter, onError]);

  // Load bans
  const loadBans = useCallback(async () => {
    setLoadingBans(true);
    setBansError(null);
    try {
      // Always fetch full ban list so toggling the filter doesn't trigger reloads.
      const bansRes = await ApiService.getSteamBans(true);
      setBans(bansRes);
    } catch (error) {
      setBansError(getErrorMessage(error));
      onError(getErrorMessage(error));
    } finally {
      setLoadingBans(false);
    }
  }, [onError]);

  // Load persistent containers (system-owned; separate list from guest live sessions)
  const loadPersistentContainers = useCallback(async () => {
    setLoadingPersistent(true);
    setPersistentError(null);
    try {
      const containers = await ApiService.getPersistentPrefillContainers();
      setPersistentContainers(containers);
    } catch (error) {
      setPersistentError(getErrorMessage(error));
      onError(getErrorMessage(error));
    } finally {
      setLoadingPersistent(false);
    }
  }, [onError]);

  // Load prefill history for a session
  const loadHistory = useCallback(
    async (sessionId: string) => {
      setLoadingHistory((prev) => new Set(prev).add(sessionId));
      try {
        const history = await ApiService.getPrefillSessionHistory(sessionId);
        setHistoryData((prev) => ({ ...prev, [sessionId]: history }));
      } catch (error) {
        onError(getErrorMessage(error));
      } finally {
        setLoadingHistory((prev) => {
          const next = new Set(prev);
          next.delete(sessionId);
          return next;
        });
      }
    },
    [onError]
  );

  // Toggle history expansion
  const toggleHistory = useCallback(
    (sessionId: string) => {
      setExpandedHistory((prev) => {
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
    },
    [historyData, loadHistory]
  );

  // Initial load
  useEffect(() => {
    loadSessions();
  }, [loadSessions]);

  useEffect(() => {
    loadBans();
  }, [loadBans]);

  useEffect(() => {
    loadPersistentContainers();
  }, [loadPersistentContainers]);

  // Live-update the persistent containers list on relevant daemon/auth SignalR events
  // (purpose-built hook, shared with the Schedules persistent card — drops in cleanly here
  // since it only depends on the global SignalR + refresh-rate contexts, not page-specific state).
  usePersistentPrefillContainerSignalR({ enabled: true, onRefresh: loadPersistentContainers });

  // SignalR subscriptions
  useEffect(() => {
    const handleSessionCreated = (session: DaemonSessionCreatedEvent) => {
      setActiveSessions((prev) => {
        if (prev.some((s) => s.id === session.id)) return prev;
        return [...prev, session as DaemonSessionDto];
      });
    };

    const handleSessionUpdated = (session: DaemonSessionUpdatedEvent) => {
      setActiveSessions((prev) =>
        prev.map((s) => (s.id === session.id ? (session as DaemonSessionDto) : s))
      );
    };

    const handleSessionTerminated = async (event: DaemonSessionTerminatedEvent) => {
      setActiveSessions((prev) => prev.filter((s) => s.id !== event.sessionId));
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
        setHistoryData((prev) => ({ ...prev, [event.sessionId]: history }));
      } catch {
        // Ignore errors in SignalR handler
      }
    };

    const handleEpicPrefillHistoryUpdated = async (event: EpicPrefillHistoryUpdatedEvent) => {
      try {
        const history = await ApiService.getPrefillSessionHistory(event.sessionId);
        setHistoryData((prev) => ({ ...prev, [event.sessionId]: history }));
      } catch {
        // Ignore errors in SignalR handler
      }
    };

    const handleBattleNetPrefillHistoryUpdated = async (event: PrefillHistoryUpdatedEvent) => {
      try {
        const history = await ApiService.getPrefillSessionHistory(event.sessionId);
        setHistoryData((prev) => ({ ...prev, [event.sessionId]: history }));
      } catch {
        // Ignore errors in SignalR handler
      }
    };

    const handleRiotPrefillHistoryUpdated = async (event: PrefillHistoryUpdatedEvent) => {
      try {
        const history = await ApiService.getPrefillSessionHistory(event.sessionId);
        setHistoryData((prev) => ({ ...prev, [event.sessionId]: history }));
      } catch {
        // Ignore errors in SignalR handler
      }
    };

    on('DaemonSessionCreated', handleSessionCreated);
    on('DaemonSessionUpdated', handleSessionUpdated);
    on('DaemonSessionTerminated', handleSessionTerminated);
    on('PrefillHistoryUpdated', handlePrefillHistoryUpdated);
    on('EpicDaemonSessionCreated', handleSessionCreated);
    on('EpicDaemonSessionUpdated', handleSessionUpdated);
    on('EpicDaemonSessionTerminated', handleSessionTerminated);
    on('EpicPrefillHistoryUpdated', handleEpicPrefillHistoryUpdated);
    on('BattleNetDaemonSessionCreated', handleSessionCreated);
    on('BattleNetDaemonSessionUpdated', handleSessionUpdated);
    on('BattleNetDaemonSessionTerminated', handleSessionTerminated);
    on('BattleNetPrefillHistoryUpdated', handleBattleNetPrefillHistoryUpdated);
    on('RiotDaemonSessionCreated', handleSessionCreated);
    on('RiotDaemonSessionUpdated', handleSessionUpdated);
    on('RiotDaemonSessionTerminated', handleSessionTerminated);
    on('RiotPrefillHistoryUpdated', handleRiotPrefillHistoryUpdated);
    on('XboxDaemonSessionCreated', handleSessionCreated);
    on('XboxDaemonSessionUpdated', handleSessionUpdated);
    on('XboxDaemonSessionTerminated', handleSessionTerminated);

    return () => {
      off('DaemonSessionCreated', handleSessionCreated);
      off('DaemonSessionUpdated', handleSessionUpdated);
      off('DaemonSessionTerminated', handleSessionTerminated);
      off('PrefillHistoryUpdated', handlePrefillHistoryUpdated);
      off('EpicDaemonSessionCreated', handleSessionCreated);
      off('EpicDaemonSessionUpdated', handleSessionUpdated);
      off('EpicDaemonSessionTerminated', handleSessionTerminated);
      off('EpicPrefillHistoryUpdated', handleEpicPrefillHistoryUpdated);
      off('BattleNetDaemonSessionCreated', handleSessionCreated);
      off('BattleNetDaemonSessionUpdated', handleSessionUpdated);
      off('BattleNetDaemonSessionTerminated', handleSessionTerminated);
      off('BattleNetPrefillHistoryUpdated', handleBattleNetPrefillHistoryUpdated);
      off('RiotDaemonSessionCreated', handleSessionCreated);
      off('RiotDaemonSessionUpdated', handleSessionUpdated);
      off('RiotDaemonSessionTerminated', handleSessionTerminated);
      off('RiotPrefillHistoryUpdated', handleRiotPrefillHistoryUpdated);
      off('XboxDaemonSessionCreated', handleSessionCreated);
      off('XboxDaemonSessionUpdated', handleSessionUpdated);
      off('XboxDaemonSessionTerminated', handleSessionTerminated);
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

  // Persistent containers are system-owned and shown in their own section below; a running
  // persistent container must not also appear (unlabeled) among the guest Live Sessions.
  const guestActiveSessions = useMemo(
    () => activeSessions.filter((s) => !s.isPersistent),
    [activeSessions]
  );

  const totalPages = Math.ceil(totalCount / pageSize);
  const activeBansCount = bans.filter((b) => b.isActive).length;
  const visibleBans = useMemo(
    () => (includeLifted ? bans : bans.filter((b) => b.isActive)),
    [bans, includeLifted]
  );
  const hasVisibleBans = visibleBans.length > 0;

  const handleRefreshAll = () => {
    loadSessions();
    loadBans();
    loadPersistentContainers();
  };

  const isRefreshing = loadingSessions || loadingBans || loadingPersistent;

  return (
    <div
      className="management-section prefill-sessions-section animate-fade-in"
      role="tabpanel"
      id="panel-prefill-sessions"
      aria-labelledby="tab-prefill-sessions"
    >
      {/* ==================== SESSIONS ==================== */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-green)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.prefillSessions.groupSessions')}
          </h3>
        </div>

        <div className="prefill-stats-grid mb-4">
          <StatCard
            icon={<Play className="w-5 h-5 icon-green" />}
            value={guestActiveSessions.length}
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

        {cacheRoute && (
          <p className="text-xs text-themed-muted mb-4">
            {cacheRoute.ip
              ? t('management.prefillSessions.cacheRoute.routesThrough', {
                  ip: cacheRoute.ip,
                  source: t(
                    `management.prefillSessions.cacheRoute.sources.${
                      CACHE_ROUTE_SOURCE_KEY[cacheRoute.source ?? ''] ?? 'unknown'
                    }`
                  )
                })
              : t('management.prefillSessions.cacheRoute.notDetermined')}
          </p>
        )}

        <div className="space-y-4">
          <AccordionSection
            title={t('management.prefillSessions.liveSessions')}
            description={t('management.prefillSessions.liveSessionsSummary')}
            count={guestActiveSessions.length}
            icon={Play}
            iconColor="var(--theme-icon-green)"
            isExpanded={liveSessionsExpanded}
            onToggle={() => setLiveSessionsExpanded(!liveSessionsExpanded)}
            badge={
              <div className="flex flex-wrap items-center gap-2 w-full justify-start sm:w-auto sm:justify-end">
                <SectionActionsMenu label={t('management.actions.menuLabel', 'Actions')}>
                  {(close) => (
                    <>
                      <ActionMenuItem
                        icon={<RefreshCw className="w-3.5 h-3.5" />}
                        disabled={isRefreshing}
                        onClick={() => {
                          handleRefreshAll();
                          close();
                        }}
                      >
                        {t('common.refresh')}
                      </ActionMenuItem>
                      {isAdmin && guestActiveSessions.length > 0 && (
                        <>
                          <ActionMenuDivider />
                          <ActionMenuDangerItem
                            icon={<StopCircle className="w-3.5 h-3.5" />}
                            disabled={terminatingAll}
                            onClick={() => {
                              setTerminateAllConfirm(true);
                              close();
                            }}
                          >
                            {t('management.prefillSessions.endAll', {
                              count: guestActiveSessions.length
                            })}
                          </ActionMenuDangerItem>
                        </>
                      )}
                    </>
                  )}
                </SectionActionsMenu>
              </div>
            }
          >
            {loadingSessions ? (
              <div className="prefill-loading-state">
                <LoadingSpinner inline size="lg" className="text-themed-muted" />
                <span>{t('management.prefillSessions.loadingSessions')}</span>
              </div>
            ) : sessionsError && guestActiveSessions.length === 0 ? (
              <PrefillErrorBlock
                title={t('management.prefillSessions.errors.loadSessions')}
                message={sessionsError}
                retryLabel={t('common.retry')}
                onRetry={loadSessions}
              />
            ) : guestActiveSessions.length === 0 ? (
              <div className="prefill-empty-state">
                <Container className="w-12 h-12 opacity-50" />
                <p className="prefill-empty-title">
                  {t('management.prefillSessions.noActiveSessions')}
                </p>
                <p className="prefill-empty-desc">
                  {t('management.prefillSessions.noActiveSessionsDesc')}
                </p>
              </div>
            ) : (
              <div className="prefill-sessions-list">
                {guestActiveSessions.map((session) => (
                  <SessionCard
                    key={session.id}
                    session={session}
                    isLive={true}
                    isAdmin={isAdmin}
                    historyData={historyData[session.id] || []}
                    isHistoryExpanded={expandedHistory.has(session.id)}
                    isLoadingHistory={loadingHistory.has(session.id)}
                    onToggleHistory={() => toggleHistory(session.id)}
                    onTerminate={() => handleTerminateSession(session.id)}
                    onBan={
                      session.id
                        ? () => setBanConfirm({ sessionId: session.id, reason: '' })
                        : undefined
                    }
                    isTerminating={terminatingSession === session.id}
                    isBanning={banningSession === session.id}
                    historyPage={historyPage[session.id] || 1}
                    onHistoryPageChange={(p) =>
                      setHistoryPage((prev) => ({ ...prev, [session.id]: p }))
                    }
                  />
                ))}
              </div>
            )}
          </AccordionSection>

          <AccordionSection
            title={t('management.prefillSessions.persistentSessions.title')}
            description={t('management.prefillSessions.persistentSessions.summary')}
            count={persistentContainers.length}
            icon={Server}
            iconColor="var(--theme-icon-blue)"
            isExpanded={persistentExpanded}
            onToggle={() => setPersistentExpanded(!persistentExpanded)}
          >
            {loadingPersistent && persistentContainers.length === 0 ? (
              <div className="prefill-loading-state">
                <LoadingSpinner inline size="lg" className="text-themed-muted" />
                <span>{t('management.prefillSessions.persistentSessions.loading')}</span>
              </div>
            ) : persistentError && persistentContainers.length === 0 ? (
              <PrefillErrorBlock
                title={t('management.prefillSessions.persistentSessions.errors.load')}
                message={persistentError}
                retryLabel={t('common.retry')}
                onRetry={loadPersistentContainers}
              />
            ) : persistentContainers.length === 0 ? (
              <div className="prefill-empty-state">
                <Server className="w-12 h-12 opacity-50" />
                <p className="prefill-empty-title">
                  {t('management.prefillSessions.persistentSessions.noContainers')}
                </p>
                <p className="prefill-empty-desc">
                  {t('management.prefillSessions.persistentSessions.noContainersDesc')}
                </p>
              </div>
            ) : (
              <div className="prefill-persistent-list">
                {persistentContainers.map((container) => (
                  <PersistentContainerCard key={container.sessionId} container={container} />
                ))}
              </div>
            )}
          </AccordionSection>
        </div>
      </div>

      {/* ==================== HISTORY ==================== */}
      <div>
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-blue)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.prefillSessions.groupHistory')}
          </h3>
        </div>

        <div className="space-y-4">
          <AccordionSection
            title={t('management.prefillSessions.sessionHistory')}
            description={t('management.prefillSessions.historySummary')}
            count={totalCount}
            icon={Clock}
            iconColor="var(--theme-icon-blue)"
            isExpanded={historyExpanded}
            onToggle={() => setHistoryExpanded(!historyExpanded)}
            badge={
              <div className="prefill-filter-inline">
                <EnhancedDropdown
                  variant="button"
                  options={
                    [
                      { value: '', label: t('management.prefillSessions.statusFilters.all') },
                      {
                        value: 'Active',
                        label: t('management.prefillSessions.statusFilters.active')
                      },
                      {
                        value: 'Terminated',
                        label: t('management.prefillSessions.statusFilters.terminated')
                      },
                      {
                        value: 'Orphaned',
                        label: t('management.prefillSessions.statusFilters.orphaned')
                      },
                      {
                        value: 'Cleaned',
                        label: t('management.prefillSessions.statusFilters.cleaned')
                      }
                    ] as DropdownOption[]
                  }
                  value={statusFilter}
                  onChange={(value: string) => {
                    // Dropdown values are fixed to '' | 'Active' | 'Terminated' | 'Orphaned' | 'Cleaned'
                    // (see options above) - narrow to PrefillSessionStatus for the typed state setter.
                    setStatusFilter(value as PrefillSessionStatus | '');
                    setPage(1);
                  }}
                  placeholder={t('management.prefillSessions.statusFilters.all')}
                  className="min-w-[90px] sm:min-w-[120px] h-10"
                  dropdownWidth="140px"
                />
                <EnhancedDropdown
                  variant="button"
                  options={
                    [
                      { value: 'all', label: t('management.prefillSessions.platformFilters.all') },
                      {
                        value: 'Steam',
                        label: t('management.prefillSessions.platformFilters.steam')
                      },
                      {
                        value: 'Epic',
                        label: t('management.prefillSessions.platformFilters.epic')
                      },
                      {
                        value: 'BattleNet',
                        label: t('management.prefillSessions.platformFilters.battlenet')
                      },
                      {
                        value: 'Riot',
                        label: t('management.prefillSessions.platformFilters.riot')
                      },
                      { value: 'Xbox', label: t('management.prefillSessions.platformFilters.xbox') }
                    ] as DropdownOption[]
                  }
                  value={platformFilter}
                  onChange={(value: string) => {
                    setPlatformFilter(value);
                    setPage(1);
                  }}
                  placeholder={t('management.prefillSessions.platformFilters.all')}
                  className="min-w-[90px] sm:min-w-[120px] h-10"
                  dropdownWidth="140px"
                />
              </div>
            }
          >
            {loadingSessions ? (
              <div className="prefill-loading-state">
                <LoadingSpinner inline size="lg" className="text-themed-muted" />
                <span>{t('management.prefillSessions.loading')}</span>
              </div>
            ) : sessionsError && sessions.length === 0 ? (
              <PrefillErrorBlock
                title={t('management.prefillSessions.errors.loadHistory')}
                message={sessionsError}
                retryLabel={t('common.retry')}
                onRetry={loadSessions}
              />
            ) : sessions.length === 0 ? (
              <div className="prefill-empty-state">
                <Clock className="w-12 h-12 opacity-50" />
                <p className="prefill-empty-title">
                  {t('management.prefillSessions.noSessionsFound')}
                </p>
                <p className="prefill-empty-desc">
                  {t('management.prefillSessions.noSessionsFoundDesc')}
                </p>
              </div>
            ) : (
              <>
                <div className="prefill-sessions-list">
                  {sessions.map((session) => (
                    <SessionCard
                      key={session.id}
                      session={session}
                      isLive={session.isLive}
                      isAdmin={isAdmin}
                      historyData={historyData[session.sessionId] || []}
                      isHistoryExpanded={expandedHistory.has(session.sessionId)}
                      isLoadingHistory={loadingHistory.has(session.sessionId)}
                      onToggleHistory={() => toggleHistory(session.sessionId)}
                      onTerminate={
                        session.isLive && !session.isPersistent
                          ? () => handleTerminateSession(session.sessionId)
                          : undefined
                      }
                      onBan={
                        session.isLive && !session.isPersistent && session.sessionId
                          ? () => setBanConfirm({ sessionId: session.sessionId, reason: '' })
                          : undefined
                      }
                      isTerminating={terminatingSession === session.sessionId}
                      isBanning={banningSession === session.sessionId}
                      historyPage={historyPage[session.sessionId] || 1}
                      onHistoryPageChange={(p) =>
                        setHistoryPage((prev) => ({ ...prev, [session.sessionId]: p }))
                      }
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

          <AccordionSection
            title={t('management.prefillSessions.bannedUsers.title')}
            description={t('management.prefillSessions.bannedUsers.summary')}
            count={activeBansCount}
            icon={Ban}
            iconColor="var(--theme-icon-red)"
            isExpanded={bansExpanded}
            onToggle={() => setBansExpanded(!bansExpanded)}
            badge={
              // h-10 matches the accordion's own chevron/badge-slot height (see Session
              // History's EnhancedDropdown pair above) - Checkbox has no explicit height of
              // its own, so without this it renders far shorter than the 40px chevron next
              // to it in the same header row.
              <div className="flex items-center h-10">
                <Checkbox
                  label={t('management.prefillSessions.bannedUsers.showLifted')}
                  checked={includeLifted}
                  onChange={(e) => setIncludeLifted(e.target.checked)}
                />
              </div>
            }
          >
            {loadingBans && !hasVisibleBans ? (
              <div className="prefill-loading-state">
                <LoadingSpinner inline size="lg" className="text-themed-muted" />
                <span>{t('management.prefillSessions.bannedUsers.loadingBans')}</span>
              </div>
            ) : bansError && !hasVisibleBans ? (
              <PrefillErrorBlock
                title={t('management.prefillSessions.errors.loadBans')}
                message={bansError}
                retryLabel={t('common.retry')}
                onRetry={loadBans}
              />
            ) : !loadingBans && !hasVisibleBans ? (
              <div className="prefill-empty-state">
                <Shield className="w-12 h-12 opacity-50" />
                <p className="prefill-empty-title">
                  {t('management.prefillSessions.bannedUsers.noBannedUsers')}
                </p>
                <p className="prefill-empty-desc">
                  {t('management.prefillSessions.bannedUsers.noBannedUsersDesc')}
                </p>
              </div>
            ) : (
              <div
                className={`prefill-bans-list ${loadingBans ? 'opacity-60 pointer-events-none' : ''}`}
              >
                {visibleBans.map((ban) => (
                  <BannedUserCard
                    key={ban.id}
                    ban={ban}
                    isAdmin={isAdmin}
                    onLiftBan={() => setLiftBanConfirm(ban)}
                    isLifting={liftingBan === ban.id}
                  />
                ))}
              </div>
            )}
          </AccordionSection>
        </div>
      </div>

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
            {t('management.prefillSessions.modals.terminateAll.message', {
              count: guestActiveSessions.length
            })}
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
              onChange={(e) =>
                banConfirm && setBanConfirm({ ...banConfirm, reason: e.target.value })
              }
              placeholder={t('management.prefillSessions.modals.ban.reasonPlaceholder')}
              className="focus-ring prefill-input"
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
                    {t('management.prefillSessions.bannedUsers.reason', {
                      reason: liftBanConfirm.banReason
                    })}
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
