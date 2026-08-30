import React, { useState, useEffect, useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Container,
  Play,
  StopCircle,
  Ban,
  Shield,
  Clock,
  ChevronDown,
  XCircle,
  Server,
  RefreshCw
} from 'lucide-react';
import { Button } from '@components/ui/Button';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { GroupHeading } from '@components/ui/GroupHeading';
import { TabPanel } from '@components/features/management/TabPanel';
import { RowActionsMenu } from '@components/ui/RowActionsMenu';
import { ActionMenuItem, ActionMenuDangerItem, ActionMenuDivider } from '@components/ui/ActionMenu';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Alert } from '@components/ui/Alert';
import { Pagination } from '@components/ui/Pagination';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Checkbox } from '@components/ui/Checkbox';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { SectionHeaderActions } from '@components/ui/SectionHeaderActions';
import Badge from '@components/ui/Badge';
import { VARIANT_BY_STATUS } from '@utils/statusVariant';
import ApiService, {
  type PrefillSessionDto,
  type DaemonSessionDto,
  type BannedPrefillUserDto,
  type PrefillHistoryEntryDto
} from '@services/api.service';
import type { PrefillSessionStatus } from '@/types/operations';
import { GAME_SERVICES, type GameServiceId } from '@/types/gameService';
import { getErrorMessage } from '@utils/error';
import { formatBytes } from '@utils/formatters';
import { FormattedTimestamp } from '@components/common/FormattedDateTime';
import { usePaginatedList } from '@hooks/usePaginatedList';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import { cleanIpAddress } from '@components/features/user/types';
import { rowToggleHandlers } from '@utils/rowToggle';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { LoadingState, EmptyState } from '@components/ui/ManagerCard';
import StatusDot from '@components/common/StatusDot';
import '../managementSectionContent.css';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import { usePersistentPrefillContainerSignalR } from '@components/features/management/schedules/scheduled-prefill/usePersistentPrefillContainerSignalR';
import type {
  DaemonSessionCreatedEvent,
  DaemonSessionUpdatedEvent,
  DaemonSessionTerminatedEvent,
  PrefillHistoryUpdatedEvent
} from '@contexts/SignalRContext/types';
import './PrefillSessionsSection.css';

interface PrefillSessionsSectionProps {
  isAdmin: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
}

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

  return (
    <Badge variant={VARIANT_BY_STATUS[effectiveStatus] ?? 'neutral'}>{getDisplayStatus()}</Badge>
  );
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

// Session lifecycle label keys for the row status dot.
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

const getStatusBadgeLabelKey = (status: string): string | null =>
  STATUS_BADGE_KEY[status.toLowerCase()] ?? null;

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
  // Downloading state flows through the unified activity registry for live daemon sessions; the DTO's
  // isPrefilling flag stays the pre-seed fallback (activity.isActive(...) || existing).
  const activity = useActivityStatus();

  // Normalize session data between DaemonSessionDto and PrefillSessionDto
  const isDaemonSession = 'id' in session && !('sessionId' in session);
  const status = session.status;
  const daemonSessionId = isDaemonSession ? (session as DaemonSessionDto).id : null;
  const isPrefilling =
    (daemonSessionId !== null &&
      activity.isActive('prefillSession', daemonSessionId, 'downloading')) ||
    (isDaemonSession ? (session as DaemonSessionDto).isPrefilling : false);

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
    ? (session as DaemonSessionDto).username || (session as DaemonSessionDto).accountUsername
    : (session as PrefillSessionDto).username || (session as PrefillSessionDto).accountUsername;
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

  const sessionTitle = displayUsername
    ? displayUsername
    : isAnonymousService
      ? t('management.prefillSessions.labels.anonymousAccount', {
          service: platformDisplayName
        })
      : isPersistentSession
        ? t('management.prefillSessions.labels.persistentContainer')
        : isAuthenticated_
          ? t('management.prefillSessions.labels.authenticatedAccount', {
              service: platformDisplayName
            })
          : t('management.prefillSessions.labels.notLoggedInSession');

  const statusLabelKey = getStatusBadgeLabelKey(status);
  const sessionDotLabel = isPrefilling
    ? t('management.prefillSessions.labels.loading')
    : statusLabelKey
      ? t(`management.prefillSessions.statusBadges.${statusLabelKey}`)
      : status;

  return (
    <div className="session-item">
      <div
        className="mgmt-row mgmt-row--interactive focus-ring--inset session-row"
        aria-expanded={isHistoryExpanded}
        {...rowToggleHandlers(onToggleHistory)}
      >
        {isPrefilling ? (
          <StatusDot tone="info" label={sessionDotLabel} />
        ) : isLive ? (
          <StatusDot state="active" label={sessionDotLabel} />
        ) : status === 'Terminated' ? (
          <StatusDot tone="error" label={sessionDotLabel} />
        ) : (
          <StatusDot tone="idle" label={sessionDotLabel} />
        )}
        <div className="mgmt-row__body">
          <div className="session-row__titleline">
            <span className="mgmt-row__title block truncate">{sessionTitle}</span>
            <Badge variant="neutral">{platformDisplayName}</Badge>
            {isPersistentSession && (
              <Badge variant="neutral">
                {t('management.prefillSessions.labels.persistentBadge')}
              </Badge>
            )}
          </div>
          <div className="mgmt-row__meta session-row__meta">
            {isPrefilling && (
              <span>
                {currentAppName || t('management.prefillSessions.labels.loading')}
                {(totalBytesTransferred ?? 0) > 0
                  ? ` · ${formatBytes(totalBytesTransferred!)}`
                  : ''}
              </span>
            )}
            {gamesCount > 0 && (
              <span className="tabular-nums">
                {t('management.prefillSessions.tooltips.gamesPrefilled', { count: gamesCount })}
              </span>
            )}
            {(totalBytesFromHistory > 0 || (!isPrefilling && (totalBytesTransferred ?? 0) > 0)) && (
              <span className="tabular-nums">
                {formatBytes(totalBytesFromHistory || totalBytesTransferred || 0)}
              </span>
            )}
            {containerName && <span className="font-mono">{containerName}</span>}
            <span>
              <FormattedTimestamp timestamp={createdAt} />
            </span>
            {endedAt && (
              <span>
                <FormattedTimestamp timestamp={endedAt} />
              </span>
            )}
            {ipAddress && <span className="font-mono">{cleanIpAddress(ipAddress)}</span>}
            {(operatingSystem || browser) && <span>{operatingSystem || browser}</span>}
          </div>
        </div>
        <div
          className="mgmt-row__actions session-row__actions"
          onClick={(e: React.MouseEvent) => e.stopPropagation()}
        >
          {isAdmin && isLive && (onBan || onTerminate) && (
            <RowActionsMenu open={menuOpen} onOpenChange={setMenuOpen}>
              {(close) => (
                <>
                  {onTerminate && (
                    <ActionMenuItem
                      onClick={() => {
                        close();
                        onTerminate();
                      }}
                      icon={<StopCircle className="w-4 h-4" />}
                      disabled={isTerminating}
                    >
                      {t('management.prefillSessions.tooltips.terminateSession')}
                    </ActionMenuItem>
                  )}
                  {onBan && (
                    <>
                      {onTerminate && <ActionMenuDivider />}
                      <ActionMenuDangerItem
                        onClick={() => {
                          close();
                          onBan();
                        }}
                        icon={<Ban className="w-4 h-4" />}
                        disabled={isBanning}
                      >
                        {t('management.prefillSessions.tooltips.banUser')}
                      </ActionMenuDangerItem>
                    </>
                  )}
                </>
              )}
            </RowActionsMenu>
          )}
        </div>
        {isLoadingHistory ? (
          <LoadingSpinner inline size="xs" />
        ) : (
          <Button
            type="button"
            variant="accordion"
            size="sm"
            open={isHistoryExpanded}
            className="session-row__chevron btn-icon-square btn-icon-square--sm pointer-target-44"
            onClick={(e) => {
              e.stopPropagation();
              onToggleHistory();
            }}
            aria-label={
              isHistoryExpanded
                ? t('ui.accordion.collapseSection')
                : t('ui.accordion.expandSection')
            }
            aria-expanded={isHistoryExpanded}
          >
            <ChevronDown
              className={`w-4 h-4 transition duration-200 ease-out${
                isHistoryExpanded ? ' rotate-180 text-themed-accent' : ' rotate-0 text-themed-muted'
              }`}
            />
          </Button>
        )}
      </div>

      <CollapsibleRegion open={isHistoryExpanded} contentClassName="mgmt-row-detail">
        <div className="session-detail">
          <p className="mgmt-subhead caps-label">
            {t('management.prefillSessions.labels.prefillHistory')}
          </p>

          {isLoadingHistory ? (
            <div className="flex items-center gap-2 text-xs text-themed-muted">
              <LoadingSpinner inline size="xs" />
              {t('management.prefillSessions.labels.loadingHistory')}
            </div>
          ) : !historyData || historyData.length === 0 ? (
            <p className="mgmt-scanmeta">
              {isLive
                ? t('management.prefillSessions.labels.noPrefillHistoryYet')
                : t('management.prefillSessions.labels.noPrefillHistoryRecorded')}
            </p>
          ) : (
            <>
              <div className="mgmt-list divided-list">
                {paginatedEntries.map((entry) => (
                  <div key={entry.id} className="mgmt-row">
                    <div className="mgmt-row__body">
                      <div className="session-row__titleline">
                        <span className="mgmt-row__title block truncate">
                          {entry.appName || t('prefill.log.unnamedApp', { appId: entry.appId })}
                        </span>
                        <HistoryStatusBadge
                          status={entry.status}
                          completedAtUtc={entry.completedAtUtc}
                        />
                      </div>
                      <div className="mgmt-row__meta session-row__meta">
                        <span>
                          {t('management.prefillSessions.historyStarted')}{' '}
                          <FormattedTimestamp timestamp={entry.startedAtUtc} />
                        </span>
                        {entry.completedAtUtc && (
                          <span>
                            {t('management.prefillSessions.historyCompleted')}{' '}
                            <FormattedTimestamp timestamp={entry.completedAtUtc} />
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
                        {entry.errorMessage && (
                          <span className="is-error">
                            <XCircle className="w-3 h-3" />
                            {entry.errorMessage}
                          </span>
                        )}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
              {totalPages > 1 && (
                <Pagination
                  currentPage={historyPage}
                  totalPages={totalPages}
                  totalItems={historyData.length}
                  itemsPerPage={historyPageSize}
                  onPageChange={onHistoryPageChange}
                  itemLabel={t('management.prefillSessions.labels.games')}
                  compact
                />
              )}
            </>
          )}
        </div>
      </CollapsibleRegion>
    </div>
  );
};

// Banned user card component
const BannedUserCard: React.FC<{
  ban: BannedPrefillUserDto;
  isAdmin: boolean;
  onLiftBan: () => void;
  isLifting: boolean;
}> = ({ ban, isAdmin, onLiftBan, isLifting }) => {
  const { t } = useTranslation();

  return (
    <div className="mgmt-row">
      <StatusDot
        tone={ban.isActive ? 'error' : 'idle'}
        label={
          ban.isActive
            ? t('management.prefillSessions.bannedUsers.active')
            : t('management.prefillSessions.bannedUsers.lifted')
        }
      />
      <div className="mgmt-row__body">
        <div className="session-row__titleline">
          <span className="mgmt-row__title block truncate">
            {ban.username || t('management.prefillSessions.bannedUsers.unknown')}
          </span>
        </div>
        <div className="mgmt-row__meta session-row__meta">
          <span>
            {t('management.prefillSessions.bannedUsers.banned', { time: '' })}
            <FormattedTimestamp timestamp={ban.bannedAtUtc} />
          </span>
          {ban.banReason && (
            <span>
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
        <div className="mgmt-row__actions">
          <Button
            variant="default"
            size="xs"
            onClick={onLiftBan}
            disabled={isLifting}
            loading={isLifting}
          >
            {t('management.prefillSessions.tooltips.liftBan')}
          </Button>
        </div>
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

  // Persistent-container run/login state now flows through the unified activity registry, keyed by the
  // lowercase platform token (BattleNet -> battlenet). The fetched container stays the pre-seed fallback
  // (activity.isActive(...) || existing).
  const activity = useActivityStatus();
  const activityPlatformKey = container.service.toLowerCase();
  const isRunning =
    activity.isActive('persistentContainer', activityPlatformKey, 'running') || container.isRunning;
  const isAuthenticated =
    activity.isActive('persistentContainer', activityPlatformKey, 'authenticated') ||
    container.isAuthenticated;

  const runTone: 'idle' | 'running' = isRunning ? 'running' : 'idle';
  const runLabel = isRunning ? t(`${baseKey}.status.running`) : t(`${baseKey}.status.stopped`);

  const showLoginState = isRunning && !isAnonymous;
  const loginLabel = container.needsRelogin
    ? t(`${baseKey}.status.needsRelogin`)
    : isAuthenticated
      ? t(`${baseKey}.status.authenticated`)
      : t(`${baseKey}.status.notLoggedIn`);

  const isPrefilling = isRunning && (container.isPrefilling ?? false);

  return (
    <div className="mgmt-row">
      <StatusDot tone={runTone} label={runLabel} />
      <div className="mgmt-row__body">
        <div className="session-row__titleline">
          <span className="mgmt-row__title">{displayName}</span>
        </div>
        <div className="mgmt-row__meta session-row__meta">
          {showLoginState && <span>{loginLabel}</span>}
          {isPrefilling && (
            <span>
              {container.currentAppName
                ? t(`${baseKey}.prefilling`, { game: container.currentAppName })
                : t(`${baseKey}.prefillingGeneric`)}
              {(container.totalBytesTransferred ?? 0) > 0
                ? ` · ${formatBytes(container.totalBytesTransferred ?? 0)}`
                : ''}
            </span>
          )}
          {container.isRunning && !isAnonymous && container.daemonAuthExpiresAtUtc && (
            <span>
              {t(`${baseKey}.tokenExpiresAt`)}{' '}
              <FormattedTimestamp timestamp={container.daemonAuthExpiresAtUtc} />
            </span>
          )}
          <span className="font-mono">{container.sessionId}</span>
        </div>
      </div>
    </div>
  );
};

const PrefillSessionsSection: React.FC<PrefillSessionsSectionProps> = ({
  isAdmin,
  onError,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { on, off, isConnected } = useSignalR();

  // Accordion states
  const [liveSessionsExpanded, setLiveSessionsExpanded] = useState(true);
  useAccordionGroupItem('prefill-live-sessions', liveSessionsExpanded, () =>
    setLiveSessionsExpanded(!liveSessionsExpanded)
  );
  const [persistentExpanded, setPersistentExpanded] = useState(true);
  useAccordionGroupItem('prefill-persistent-sessions', persistentExpanded, () =>
    setPersistentExpanded(!persistentExpanded)
  );
  const [historyExpanded, setHistoryExpanded] = useState(true);
  useAccordionGroupItem('prefill-session-history', historyExpanded, () =>
    setHistoryExpanded(!historyExpanded)
  );
  const [bansExpanded, setBansExpanded] = useState(true);
  useAccordionGroupItem('prefill-banned-users', bansExpanded, () => setBansExpanded(!bansExpanded));

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
  const [hasLoadedSessions, setHasLoadedSessions] = useState(false);
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
  const [bans, setBans] = useState<BannedPrefillUserDto[]>([]);
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
  const [liftBanConfirm, setLiftBanConfirm] = useState<BannedPrefillUserDto | null>(null);

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
      setHasLoadedSessions(true);

      // Pre-fetch history for all sessions
      const historySessionIds = [
        ...sessionsRes.sessions.map((session) => session.sessionId),
        ...activeRes.map((session) => session.id)
      ];
      const historyPromises = historySessionIds.map(async (sessionId) => {
        try {
          const history = await ApiService.getPrefillSessionHistory(sessionId);
          return { sessionId, history };
        } catch {
          return { sessionId, history: [] };
        }
      });

      Promise.all(historyPromises).then((results) => {
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
      const bansRes = await ApiService.getPrefillBans(true);
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

  // Recover a stale snapshot after a reconnect: a session change/completion event can be
  // missed while the socket is down, so resync the sessions view whenever the connection returns.
  useReconnectRefetch(isConnected, () => loadSessions());

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

    on('DaemonSessionCreated', handleSessionCreated);
    on('DaemonSessionUpdated', handleSessionUpdated);
    on('DaemonSessionTerminated', handleSessionTerminated);
    on('PrefillHistoryUpdated', handlePrefillHistoryUpdated);
    on('EpicDaemonSessionCreated', handleSessionCreated);
    on('EpicDaemonSessionUpdated', handleSessionUpdated);
    on('EpicDaemonSessionTerminated', handleSessionTerminated);
    on('EpicPrefillHistoryUpdated', handlePrefillHistoryUpdated);
    on('BattleNetDaemonSessionCreated', handleSessionCreated);
    on('BattleNetDaemonSessionUpdated', handleSessionUpdated);
    on('BattleNetDaemonSessionTerminated', handleSessionTerminated);
    on('BattleNetPrefillHistoryUpdated', handlePrefillHistoryUpdated);
    on('RiotDaemonSessionCreated', handleSessionCreated);
    on('RiotDaemonSessionUpdated', handleSessionUpdated);
    on('RiotDaemonSessionTerminated', handleSessionTerminated);
    on('RiotPrefillHistoryUpdated', handlePrefillHistoryUpdated);
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
      off('EpicPrefillHistoryUpdated', handlePrefillHistoryUpdated);
      off('BattleNetDaemonSessionCreated', handleSessionCreated);
      off('BattleNetDaemonSessionUpdated', handleSessionUpdated);
      off('BattleNetDaemonSessionTerminated', handleSessionTerminated);
      off('BattleNetPrefillHistoryUpdated', handlePrefillHistoryUpdated);
      off('RiotDaemonSessionCreated', handleSessionCreated);
      off('RiotDaemonSessionUpdated', handleSessionUpdated);
      off('RiotDaemonSessionTerminated', handleSessionTerminated);
      off('RiotPrefillHistoryUpdated', handlePrefillHistoryUpdated);
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
      await ApiService.banPrefillUserBySession(
        banConfirm.sessionId,
        banConfirm.reason || undefined
      );
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
      await ApiService.liftPrefillBan(banId);
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

  const liveSessionsHelpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.prefillSessions.help.liveSessionsTitle')}>
        {t('management.prefillSessions.liveSessionsSummary')}
      </HelpSection>
    </HelpPopover>
  );

  const persistentSessionsHelpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.prefillSessions.persistentSessions.help.aboutTitle')}>
        {t('management.prefillSessions.persistentSessions.summary')}
      </HelpSection>
    </HelpPopover>
  );

  const historyHelpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.prefillSessions.help.historyTitle')}>
        {t('management.prefillSessions.historySummary')}
      </HelpSection>
    </HelpPopover>
  );

  const bannedUsersHelpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.prefillSessions.bannedUsers.help.aboutTitle')}>
        {t('management.prefillSessions.bannedUsers.summary')}
      </HelpSection>
    </HelpPopover>
  );

  return (
    <TabPanel tabId="prefill-sessions" className="prefill-sessions-section">
      {/* ==================== SESSIONS ==================== */}
      <div className="mb-6 sm:mb-8">
        <GroupHeading
          label={t('management.sections.prefillSessions.groupSessions')}
          actions={<AccordionGroupToggle />}
        />

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
            titleAccessory={liveSessionsHelpAccessory}
            count={guestActiveSessions.length}
            icon={Play}
            isExpanded={liveSessionsExpanded}
            onToggle={() => setLiveSessionsExpanded(!liveSessionsExpanded)}
            badge={
              <SectionHeaderActions>
                <SectionActionsMenu label={t('management.actions.menuLabel')}>
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
              </SectionHeaderActions>
            }
          >
            {loadingSessions && !hasLoadedSessions ? (
              <div className="w-full">
                <LoadingState
                  message={t('management.prefillSessions.loadingSessions')}
                  shape="rows"
                  rows={4}
                />
              </div>
            ) : sessionsError && guestActiveSessions.length === 0 ? (
              <ErrorBlock
                title={t('management.prefillSessions.errors.loadSessions')}
                message={sessionsError}
                retryLabel={t('common.retry')}
                onRetry={loadSessions}
              />
            ) : guestActiveSessions.length === 0 ? (
              <EmptyState
                icon={Container}
                title={t('management.prefillSessions.noActiveSessions')}
                subtitle={t('management.prefillSessions.noActiveSessionsDesc')}
              />
            ) : (
              <div className="mgmt-list divided-list">
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
            titleAccessory={persistentSessionsHelpAccessory}
            count={persistentContainers.length}
            icon={Server}
            isExpanded={persistentExpanded}
            onToggle={() => setPersistentExpanded(!persistentExpanded)}
          >
            {loadingPersistent && persistentContainers.length === 0 ? (
              <div className="w-full">
                <LoadingState
                  message={t('management.prefillSessions.persistentSessions.loading')}
                  shape="cards"
                  rows={3}
                />
              </div>
            ) : persistentError && persistentContainers.length === 0 ? (
              <ErrorBlock
                title={t('management.prefillSessions.persistentSessions.errors.load')}
                message={persistentError}
                retryLabel={t('common.retry')}
                onRetry={loadPersistentContainers}
              />
            ) : persistentContainers.length === 0 ? (
              <EmptyState
                icon={Server}
                title={t('management.prefillSessions.persistentSessions.noContainers')}
                subtitle={t('management.prefillSessions.persistentSessions.noContainersDesc')}
              />
            ) : (
              <div className="mgmt-list divided-list">
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
        <GroupHeading label={t('management.sections.prefillSessions.groupHistory')} />

        <div className="space-y-4">
          <AccordionSection
            title={t('management.prefillSessions.sessionHistory')}
            titleAccessory={historyHelpAccessory}
            count={totalCount}
            icon={Clock}
            isExpanded={historyExpanded}
            onToggle={() => setHistoryExpanded(!historyExpanded)}
          >
            <div className="prefill-history-filters">
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
                className="prefill-filter-status"
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
                className="prefill-filter-platform"
                dropdownWidth="140px"
              />
            </div>
            {loadingSessions && !hasLoadedSessions ? (
              <div className="w-full">
                <LoadingState
                  message={t('management.prefillSessions.loading')}
                  shape="rows"
                  rows={5}
                />
              </div>
            ) : sessionsError && sessions.length === 0 ? (
              <ErrorBlock
                title={t('management.prefillSessions.errors.loadHistory')}
                message={sessionsError}
                retryLabel={t('common.retry')}
                onRetry={loadSessions}
              />
            ) : sessions.length === 0 ? (
              <EmptyState
                icon={Clock}
                title={t('management.prefillSessions.noSessionsFound')}
                subtitle={t('management.prefillSessions.noSessionsFoundDesc')}
              />
            ) : (
              <>
                <div className="mgmt-list divided-list">
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
                      itemLabel={t('management.prefillSessions.labels.sessions')}
                    />
                  </div>
                )}
              </>
            )}
          </AccordionSection>

          <AccordionSection
            title={t('management.prefillSessions.bannedUsers.title')}
            titleAccessory={bannedUsersHelpAccessory}
            count={activeBansCount}
            icon={Ban}
            isExpanded={bansExpanded}
            onToggle={() => setBansExpanded(!bansExpanded)}
            badge={
              <SectionHeaderActions>
                {/* h-10 matches the accordion's own chevron/badge-slot height (see Session
                    History's EnhancedDropdown pair above) - Checkbox has no explicit height of
                    its own, so without this it renders far shorter than the 40px chevron next
                    to it in the same header row. */}
                <div className="flex items-center h-10">
                  <Checkbox
                    label={t('management.prefillSessions.bannedUsers.showLifted')}
                    checked={includeLifted}
                    onChange={(e) => setIncludeLifted(e.target.checked)}
                  />
                </div>
              </SectionHeaderActions>
            }
          >
            {loadingBans && !hasVisibleBans ? (
              <div className="w-full">
                <LoadingState
                  message={t('management.prefillSessions.bannedUsers.loadingBans')}
                  shape="rows"
                  rows={3}
                />
              </div>
            ) : bansError && !hasVisibleBans ? (
              <ErrorBlock
                title={t('management.prefillSessions.errors.loadBans')}
                message={bansError}
                retryLabel={t('common.retry')}
                onRetry={loadBans}
              />
            ) : !loadingBans && !hasVisibleBans ? (
              <EmptyState
                icon={Shield}
                title={t('management.prefillSessions.bannedUsers.noBannedUsers')}
                subtitle={t('management.prefillSessions.bannedUsers.noBannedUsersDesc')}
              />
            ) : (
              <div
                className={`mgmt-list divided-list ${loadingBans ? 'opacity-60 pointer-events-none' : ''}`}
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
      <ConfirmationModal
        opened={terminateAllConfirm}
        onClose={() => setTerminateAllConfirm(false)}
        onConfirm={handleTerminateAll}
        title={t('management.prefillSessions.modals.terminateAll.title')}
        confirmLabel={t('management.prefillSessions.modals.terminateAll.confirm')}
        loading={terminatingAll}
      >
        <p className="text-themed-secondary">
          {t('management.prefillSessions.modals.terminateAll.message', {
            count: guestActiveSessions.length
          })}
        </p>
        <Alert color="yellow">
          <p className="text-sm">{t('management.prefillSessions.modals.terminateAll.warning')}</p>
        </Alert>
      </ConfirmationModal>

      {/* Ban Confirmation Modal */}
      <ConfirmationModal
        opened={banConfirm !== null}
        onClose={() => setBanConfirm(null)}
        onConfirm={handleBanBySession}
        title={t('management.prefillSessions.modals.ban.title')}
        icon={<Ban className="w-6 h-6 text-themed-error" />}
        confirmLabel={t('management.prefillSessions.modals.ban.confirm')}
        loading={banningSession !== null}
      >
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
            className="focus-ring prefill-input"
          />
        </div>
        <Alert color="red">
          <p className="text-sm">{t('management.prefillSessions.modals.ban.warning')}</p>
        </Alert>
      </ConfirmationModal>

      {/* Lift Ban Confirmation Modal - a shield rather than a warning, since restoring access is
          the one non-destructive confirmation on this card. */}
      <ConfirmationModal
        opened={liftBanConfirm !== null}
        onClose={() => setLiftBanConfirm(null)}
        onConfirm={() => {
          if (liftBanConfirm) {
            void handleLiftBan(liftBanConfirm.id);
          }
        }}
        title={t('management.prefillSessions.modals.liftBan.title')}
        icon={<Shield className="w-6 h-6 text-themed-primary" />}
        confirmLabel={t('management.prefillSessions.modals.liftBan.confirm')}
        confirmColor="blue"
        loading={liftingBan !== null}
      >
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
      </ConfirmationModal>
    </TabPanel>
  );
};

export default PrefillSessionsSection;
