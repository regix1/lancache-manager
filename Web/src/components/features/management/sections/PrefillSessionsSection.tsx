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
  Hash
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
  BannedSteamUserDto
} from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';

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
            className="px-1.5 py-0.5 rounded text-xs font-medium flex items-center gap-1"
            style={{
              backgroundColor: 'color-mix(in srgb, var(--theme-icon-green) 20%, transparent)',
              color: 'var(--theme-icon-green)'
            }}
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

  // Load sessions
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

  // Initial load
  useEffect(() => {
    loadSessions();
  }, [loadSessions]);

  useEffect(() => {
    loadBans();
  }, [loadBans]);

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
            <div
              className="w-12 h-12 rounded-lg flex items-center justify-center"
              style={{ backgroundColor: 'color-mix(in srgb, var(--theme-icon-green) 20%, transparent)' }}
            >
              <Play className="w-6 h-6" style={{ color: 'var(--theme-icon-green)' }} />
            </div>
            <div>
              <div className="text-2xl font-bold text-themed-primary">{activeSessions.length}</div>
              <div className="text-sm text-themed-muted">Active Sessions</div>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardContent className="py-4 flex items-center gap-4">
            <div
              className="w-12 h-12 rounded-lg flex items-center justify-center"
              style={{ backgroundColor: 'color-mix(in srgb, var(--theme-primary) 20%, transparent)' }}
            >
              <Container className="w-6 h-6" style={{ color: 'var(--theme-primary)' }} />
            </div>
            <div>
              <div className="text-2xl font-bold text-themed-primary">{totalCount}</div>
              <div className="text-sm text-themed-muted">Total Sessions</div>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardContent className="py-4 flex items-center gap-4">
            <div
              className="w-12 h-12 rounded-lg flex items-center justify-center"
              style={{ backgroundColor: 'color-mix(in srgb, var(--theme-icon-red) 20%, transparent)' }}
            >
              <Ban className="w-6 h-6" style={{ color: 'var(--theme-icon-red)' }} />
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
          <div
            className="w-1 h-5 rounded-full"
            style={{ backgroundColor: 'var(--theme-icon-green)' }}
          />
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
          <div className="grid gap-4">
            {activeSessions.map(session => (
              <Card key={session.id}>
                <CardContent className="py-4">
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-4">
                      <div
                        className="w-10 h-10 rounded-lg flex items-center justify-center"
                        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                      >
                        <Container className="w-5 h-5 text-themed-muted" />
                      </div>
                      <div>
                        <div className="flex items-center gap-2">
                          <span className="font-mono text-sm text-themed-primary">
                            {session.containerName}
                          </span>
                          <StatusBadge status={session.status} isLive />
                        </div>
                        <div className="flex items-center gap-3 text-xs text-themed-muted mt-1">
                          <span className="flex items-center gap-1">
                            <Clock className="w-3 h-3" />
                            Started: <FormattedTimestamp timestamp={session.createdAt} />
                          </span>
                          {session.isPrefilling && (
                            <span
                              className="flex items-center gap-1 px-1.5 py-0.5 rounded"
                              style={{
                                backgroundColor: 'color-mix(in srgb, var(--theme-primary) 20%, transparent)',
                                color: 'var(--theme-primary)'
                              }}
                            >
                              Prefilling
                            </span>
                          )}
                        </div>
                      </div>
                    </div>

                    {isAuthenticated && (
                      <div className="flex items-center gap-2">
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
                      </div>
                    )}
                  </div>
                </CardContent>
              </Card>
            ))}
          </div>
        )}
      </div>

      {/* Session History */}
      <div className="mb-8">
        <div className="flex items-center justify-between mb-4">
          <div className="flex items-center gap-2">
            <div
              className="w-1 h-5 rounded-full"
              style={{ backgroundColor: 'var(--theme-primary)' }}
            />
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
            <div className="space-y-2">
              {sessions.map(session => (
                <Card key={session.id}>
                  <CardContent className="py-3">
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-4">
                        <div
                          className="w-8 h-8 rounded flex items-center justify-center"
                          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                        >
                          <Container className="w-4 h-4 text-themed-muted" />
                        </div>
                        <div>
                          <div className="flex items-center gap-2">
                            <span className="font-mono text-sm text-themed-primary">
                              {session.containerName || session.sessionId.slice(0, 8)}
                            </span>
                            <StatusBadge status={session.status} isLive={session.isLive} />
                            {session.isAuthenticated && (
                              <Tooltip content="Steam authenticated">
                                <CheckCircle className="w-4 h-4" style={{ color: 'var(--theme-icon-green)' }} />
                              </Tooltip>
                            )}
                          </div>
                          <div className="flex items-center gap-3 text-xs text-themed-muted mt-1">
                            <span>
                              Created: <FormattedTimestamp timestamp={session.createdAtUtc} />
                            </span>
                            {session.endedAtUtc && (
                              <span>
                                Ended: <FormattedTimestamp timestamp={session.endedAtUtc} />
                              </span>
                            )}
                            {session.steamUsernameHash && (
                              <Tooltip content={`Hash: ${session.steamUsernameHash.slice(0, 16)}...`}>
                                <span className="flex items-center gap-1">
                                  <Hash className="w-3 h-3" />
                                  User linked
                                </span>
                              </Tooltip>
                            )}
                          </div>
                        </div>
                      </div>

                      {isAuthenticated && session.isLive && (
                        <div className="flex items-center gap-2">
                          {session.steamUsernameHash && (
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
                        </div>
                      )}
                    </div>
                  </CardContent>
                </Card>
              ))}
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
            <div
              className="w-1 h-5 rounded-full"
              style={{ backgroundColor: 'var(--theme-icon-red)' }}
            />
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
                          <Tooltip content={ban.usernameHash}>
                            <span className="font-mono text-sm text-themed-primary">
                              {ban.usernameHash.slice(0, 16)}...
                            </span>
                          </Tooltip>
                          {ban.isActive ? (
                            <span
                              className="px-2 py-0.5 rounded text-xs font-medium"
                              style={{
                                backgroundColor: 'var(--theme-icon-red)',
                                color: '#fff'
                              }}
                            >
                              Active
                            </span>
                          ) : (
                            <span
                              className="px-2 py-0.5 rounded text-xs font-medium"
                              style={{
                                backgroundColor: 'var(--theme-text-muted)',
                                color: '#fff'
                              }}
                            >
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
              className="w-full px-3 py-2 rounded text-sm"
              style={{
                backgroundColor: 'var(--theme-bg-tertiary)',
                color: 'var(--theme-text-primary)',
                border: '1px solid var(--theme-border-primary)'
              }}
            />
          </div>

          <Alert color="red">
            <p className="text-sm">
              The ban is based on the Steam username hash. The same user cannot create a new session.
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
            <div
              className="p-3 rounded"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              <div className="text-sm">
                <div className="flex items-center gap-2">
                  <Hash className="w-4 h-4 text-themed-muted" />
                  <span className="font-mono text-themed-primary">
                    {liftBanConfirm.usernameHash.slice(0, 24)}...
                  </span>
                </div>
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
