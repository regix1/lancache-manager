import React, { useEffect, useState } from 'react';
import { Users, User, Trash2, RefreshCw, Loader2, AlertTriangle, Clock, Network, Monitor, Globe } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import ApiService from '@services/api.service';

interface Session {
  id: string;
  deviceId?: string | null; // Browser fingerprint device ID
  deviceName: string | null;
  ipAddress: string | null;
  localIp: string | null;
  hostname: string | null;
  operatingSystem: string | null;
  browser: string | null;
  createdAt: string;
  lastSeenAt: string | null;
  expiresAt: string;
  isExpired: boolean;
  isRevoked: boolean;
  revokedAt?: string | null;
  revokedBy?: string | null;
  type: 'authenticated' | 'guest';
}

const UserTab: React.FC = () => {
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [revokingSession, setRevokingSession] = useState<string | null>(null);
  const [deletingSession, setDeletingSession] = useState<string | null>(null);
  const [showRevokeModal, setShowRevokeModal] = useState(false);
  const [showDeleteModal, setShowDeleteModal] = useState(false);
  const [pendingRevokeSession, setPendingRevokeSession] = useState<Session | null>(null);
  const [pendingDeleteSession, setPendingDeleteSession] = useState<Session | null>(null);

  const loadSessions = async (showLoading = false) => {
    try {
      if (showLoading) {
        setLoading(true);
      }
      setError(null);
      const response = await fetch('/api/auth/sessions', {
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        const data = await response.json();
        setSessions(data.sessions || []);
      } else {
        const errorData = await response.json();
        setError(errorData.error || 'Failed to load sessions');
      }
    } catch (err: any) {
      setError(err.message || 'Failed to load sessions');
    } finally {
      if (showLoading) {
        setLoading(false);
      }
    }
  };

  useEffect(() => {
    // Initial load with loading spinner
    loadSessions(true);

    // Live refresh every 3 seconds for near-realtime updates (without loading spinner)
    const refreshInterval = setInterval(() => {
      loadSessions(false);
    }, 3000);

    // Cleanup interval on unmount
    return () => {
      clearInterval(refreshInterval);
    };
  }, []);

  const handleRevokeSession = (session: Session) => {
    setPendingRevokeSession(session);
    setShowRevokeModal(true);
  };

  const confirmRevokeSession = async () => {
    if (!pendingRevokeSession) return;

    try {
      setRevokingSession(pendingRevokeSession.id);
      const endpoint = pendingRevokeSession.type === 'authenticated'
        ? `/api/auth/devices/${encodeURIComponent(pendingRevokeSession.id)}`
        : `/api/auth/guest/${encodeURIComponent(pendingRevokeSession.id)}/revoke`;

      const response = await fetch(endpoint, {
        method: pendingRevokeSession.type === 'authenticated' ? 'DELETE' : 'POST',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        await loadSessions(false);
        setShowRevokeModal(false);
        setPendingRevokeSession(null);
      } else {
        const errorData = await response.json();
        alert(errorData.message || errorData.error || 'Failed to revoke session');
      }
    } catch (err: any) {
      alert(err.message || 'Failed to revoke session');
    } finally {
      setRevokingSession(null);
    }
  };

  const handleDeleteSession = (session: Session) => {
    setPendingDeleteSession(session);
    setShowDeleteModal(true);
  };

  const confirmDeleteSession = async () => {
    if (!pendingDeleteSession) return;

    try {
      setDeletingSession(pendingDeleteSession.id);
      const endpoint = pendingDeleteSession.type === 'authenticated'
        ? `/api/auth/devices/${encodeURIComponent(pendingDeleteSession.id)}`
        : `/api/auth/guest/${encodeURIComponent(pendingDeleteSession.id)}`;

      const response = await fetch(endpoint, {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        await loadSessions(false);
        setShowDeleteModal(false);
        setPendingDeleteSession(null);
      } else {
        const errorData = await response.json();
        alert(errorData.message || errorData.error || 'Failed to delete session');
      }
    } catch (err: any) {
      alert(err.message || 'Failed to delete session');
    } finally {
      setDeletingSession(null);
    }
  };

  const formatDate = (dateString: string | null) => {
    if (!dateString) return 'Never';
    const date = new Date(dateString);
    return date.toLocaleString();
  };

  const formatTimeRemaining = (expiresAt: string) => {
    const now = new Date();
    const expiry = new Date(expiresAt);
    const diff = expiry.getTime() - now.getTime();

    if (diff <= 0) return 'Expired';

    const hours = Math.floor(diff / (1000 * 60 * 60));
    const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));

    if (hours > 0) {
      return `${hours}h ${minutes}m remaining`;
    }
    return `${minutes}m remaining`;
  };

  return (
    <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6 space-y-4 sm:space-y-6">
      {/* Header */}
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-center gap-3 min-w-0 flex-1">
          <div className="p-2 rounded-lg flex-shrink-0" style={{ backgroundColor: 'var(--theme-primary-subtle)' }}>
            <Users className="w-6 h-6" style={{ color: 'var(--theme-primary)' }} />
          </div>
          <div className="min-w-0">
            <h1 className="text-xl sm:text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>User Management</h1>
            <p className="text-xs sm:text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
              Manage all users and sessions • Live refresh
            </p>
          </div>
        </div>
        <button
          onClick={() => loadSessions(true)}
          disabled={loading}
          className="p-2 rounded-lg transition-colors disabled:opacity-50 flex items-center justify-center flex-shrink-0"
          style={{
            color: 'var(--theme-text-muted)',
            backgroundColor: 'transparent'
          }}
          onMouseEnter={(e) =>
            !loading && (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
          }
          onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
          title="Refresh sessions"
        >
          {loading ? (
            <Loader2 className="w-4 h-4 animate-spin" />
          ) : (
            <RefreshCw className="w-4 h-4" />
          )}
        </button>
      </div>

      {/* Stats */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <Card>
          <div className="p-4">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>Total Users</p>
                <p className="text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>{sessions.length}</p>
              </div>
              <Users className="w-8 h-8" style={{ color: 'var(--theme-primary)' }} />
            </div>
          </div>
        </Card>
        <Card>
          <div className="p-4">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>Authenticated</p>
                <p className="text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>
                  {sessions.filter(s => s.type === 'authenticated').length}
                </p>
              </div>
              <User className="w-8 h-8" style={{ color: 'var(--theme-user-session)' }} />
            </div>
          </div>
        </Card>
        <Card>
          <div className="p-4">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>Guests</p>
                <p className="text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>
                  {sessions.filter(s => s.type === 'guest').length}
                </p>
              </div>
              <User className="w-8 h-8" style={{ color: 'var(--theme-guest-session)' }} />
            </div>
          </div>
        </Card>
      </div>

      {/* Users Table */}
      <Card>
        <div className="p-6">
          <h2 className="text-lg font-semibold mb-4" style={{ color: 'var(--theme-text-primary)' }}>
            All Sessions
          </h2>

          {loading && (
            <div className="text-center py-8">
              <Loader2
                className="w-8 h-8 animate-spin mx-auto"
                style={{ color: 'var(--theme-text-muted)' }}
              />
              <p className="text-sm mt-2" style={{ color: 'var(--theme-text-secondary)' }}>
                Loading sessions...
              </p>
            </div>
          )}

          {error && (
            <div
              className="p-4 rounded-lg"
              style={{
                backgroundColor: 'var(--theme-error-bg)',
                border: '1px solid var(--theme-error)'
              }}
            >
              <p className="text-sm" style={{ color: 'var(--theme-error-text)' }}>{error}</p>
            </div>
          )}

          {!loading && !error && sessions.length === 0 && (
            <div className="text-center py-8">
              <Users
                className="w-12 h-12 mx-auto mb-2"
                style={{ color: 'var(--theme-text-muted)' }}
              />
              <p style={{ color: 'var(--theme-text-secondary)' }}>No active sessions</p>
            </div>
          )}

          {!loading && !error && sessions.length > 0 && (
            <div className="space-y-3">
              {sessions.map((session) => (
                <div
                  key={session.id}
                  className="p-3 sm:p-4 rounded-lg"
                  style={{
                    backgroundColor: (session.isExpired || session.isRevoked) ? 'var(--theme-bg-tertiary)' : 'var(--theme-bg-secondary)',
                    border: '1px solid var(--theme-border)'
                  }}
                >
                  <div className="flex flex-col sm:flex-row items-start justify-between gap-4">
                    <div
                      className="flex items-center gap-3 flex-1 min-w-0 w-full sm:w-auto"
                      style={{
                        opacity: (session.isExpired || session.isRevoked) ? 0.6 : 1
                      }}
                    >
                      <div
                        className="p-2 rounded-lg flex-shrink-0"
                        style={{
                          backgroundColor: session.type === 'authenticated'
                            ? 'var(--theme-user-session-bg)'
                            : 'var(--theme-guest-session-bg)'
                        }}
                      >
                        <User
                          className="w-5 h-5"
                          style={{
                            color: session.type === 'authenticated'
                              ? 'var(--theme-user-session)'
                              : 'var(--theme-guest-session)'
                          }}
                        />
                      </div>
                      <div className="flex-1 min-w-0">
                        <div className="grid gap-2">
                          <div className="flex items-center gap-2 flex-wrap">
                            <h3 className="font-semibold truncate" style={{ color: 'var(--theme-text-primary)' }}>
                              {session.deviceName || 'Unknown Device'}
                            </h3>
                            {session.type === 'authenticated' && (
                              <span
                                className="px-2 py-0.5 text-xs rounded font-medium"
                                style={{
                                  backgroundColor: 'var(--theme-user-session-bg)',
                                  color: 'var(--theme-user-session)'
                                }}
                              >
                                USER
                              </span>
                            )}
                            {session.type === 'guest' && (
                              <span
                                className="px-2 py-0.5 text-xs rounded font-medium"
                                style={{
                                  backgroundColor: 'var(--theme-guest-session-bg)',
                                  color: 'var(--theme-guest-session)'
                                }}
                              >
                                GUEST
                              </span>
                            )}
                            {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                              <span
                                className="px-2 py-0.5 text-xs rounded font-medium"
                                style={{
                                  backgroundColor: 'var(--theme-warning-bg)',
                                  color: 'var(--theme-warning-text)'
                                }}
                              >
                                {formatTimeRemaining(session.expiresAt)}
                              </span>
                            )}
                            {session.isRevoked && (
                              <span
                                className="px-2 py-0.5 text-xs rounded font-medium"
                                style={{
                                  backgroundColor: 'var(--theme-error-bg)',
                                  color: 'var(--theme-error-text)'
                                }}
                              >
                                Revoked
                              </span>
                            )}
                            {session.isExpired && !session.isRevoked && (
                              <span
                                className="px-2 py-0.5 text-xs rounded font-medium"
                                style={{
                                  backgroundColor: 'var(--theme-warning-bg)',
                                  color: 'var(--theme-warning-text)'
                                }}
                              >
                                Expired
                              </span>
                            )}
                          </div>

                          {/* Metadata Grid - Clean 3-column layout */}
                          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2 text-xs sm:text-sm w-full">
                            {/* IP Address */}
                            {session.ipAddress && (
                              <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                                <Network className="w-4 h-4 flex-shrink-0" />
                                <span className="truncate" title={session.localIp ? `Local IP: ${session.localIp}` : undefined}>
                                  {(() => {
                                    const cleanIp = session.ipAddress.replace('::ffff:', '');
                                    if (cleanIp === '::1' || cleanIp === '127.0.0.1') {
                                      return 'localhost';
                                    }
                                    return cleanIp;
                                  })()}
                                </span>
                              </div>
                            )}

                            {/* Operating System */}
                            {session.operatingSystem && (
                              <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                                <Monitor className="w-4 h-4 flex-shrink-0" />
                                <span className="truncate" title={session.operatingSystem}>{session.operatingSystem}</span>
                              </div>
                            )}

                            {/* Browser */}
                            {session.browser && (
                              <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                                <Globe className="w-4 h-4 flex-shrink-0" />
                                <span className="truncate" title={session.browser}>{session.browser}</span>
                              </div>
                            )}

                            {/* Created */}
                            <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                              <Clock className="w-4 h-4 flex-shrink-0" />
                              <span className="truncate" title={`Created: ${formatDate(session.createdAt)}`}>Created: {formatDate(session.createdAt)}</span>
                            </div>

                            {/* Last seen */}
                            {session.lastSeenAt && (
                              <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                                <Clock className="w-4 h-4 flex-shrink-0" />
                                <span className="truncate" title={`Last seen: ${formatDate(session.lastSeenAt)}`}>Last seen: {formatDate(session.lastSeenAt)}</span>
                              </div>
                            )}

                            {/* Revoked (guests only) */}
                            {session.revokedAt && session.type === 'guest' && (
                              <div className="flex items-center gap-2" style={{ color: 'var(--theme-error-text)' }}>
                                <Clock className="w-4 h-4 flex-shrink-0" />
                                <span className="truncate" title={`Revoked: ${formatDate(session.revokedAt)}`}>Revoked: {formatDate(session.revokedAt)}</span>
                              </div>
                            )}

                            {/* Revoked by (guests only) */}
                            {session.revokedBy && session.type === 'guest' && (
                              <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                                <User className="w-4 h-4 flex-shrink-0" />
                                <span className="truncate" title={`Revoked by: ${(() => {
                                  const cleanIp = session.revokedBy.replace('::ffff:', '');
                                  if (cleanIp === '::1' || cleanIp === '127.0.0.1') {
                                    return 'localhost';
                                  }
                                  return cleanIp;
                                })()}`}>
                                  Revoked by: {(() => {
                                    const cleanIp = session.revokedBy.replace('::ffff:', '');
                                    if (cleanIp === '::1' || cleanIp === '127.0.0.1') {
                                      return 'localhost';
                                    }
                                    return cleanIp;
                                  })()}
                                </span>
                              </div>
                            )}
                          </div>

                          {/* ID Display - Combined for authenticated users, separate for guests */}
                          {session.type === 'authenticated' ? (
                            // Authenticated users: Device ID and Session ID are the same
                            <div
                              className="text-xs font-mono truncate overflow-x-auto"
                              style={{ color: 'var(--theme-text-muted)' }}
                              title={`Device/Session ID: ${session.id}`}
                            >
                              Device/Session ID: {session.id}
                            </div>
                          ) : (
                            // Guest users: Show both IDs separately
                            <>
                              {session.deviceId && (
                                <div
                                  className="text-xs font-mono truncate overflow-x-auto"
                                  style={{ color: 'var(--theme-text-muted)' }}
                                  title={`Device ID: ${session.deviceId}`}
                                >
                                  Device ID: {session.deviceId}
                                </div>
                              )}
                              <div
                                className="text-xs font-mono truncate overflow-x-auto"
                                style={{ color: 'var(--theme-text-muted)' }}
                                title={`Session ID: ${session.id}`}
                              >
                                Session ID: {session.id}
                              </div>
                            </>
                          )}
                        </div>
                      </div>
                    </div>

                    <div className="flex gap-2 items-start justify-end w-full sm:w-auto sm:min-w-[180px]">
                      {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                        <Button
                          variant="default"
                          color="orange"
                          size="sm"
                          onClick={() => handleRevokeSession(session)}
                          disabled={revokingSession === session.id}
                        >
                          {revokingSession === session.id ? 'Revoking...' : 'Revoke'}
                        </Button>
                      )}
                      <Button
                        variant="default"
                        color="red"
                        size="sm"
                        leftSection={<Trash2 className="w-4 h-4" />}
                        onClick={() => handleDeleteSession(session)}
                        disabled={deletingSession === session.id}
                        style={(session.isExpired || session.isRevoked) ? {
                          backgroundColor: 'var(--theme-bg-secondary)',
                          borderColor: 'var(--theme-error)'
                        } : undefined}
                      >
                        {deletingSession === session.id ? 'Deleting...' : 'Delete'}
                      </Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </Card>

      {/* Info Card */}
      <Card className="about-section">
        <div
          className="p-3 sm:p-4 border-l-4"
          style={{
            backgroundColor: 'var(--theme-info-bg)',
            borderLeftColor: 'var(--theme-info)'
          }}
        >
          <div className="flex gap-2 sm:gap-3">
            <AlertTriangle className="w-4 h-4 sm:w-5 sm:h-5 flex-shrink-0 mt-0.5" style={{ color: 'var(--theme-info)' }} />
            <div className="text-xs sm:text-sm min-w-0" style={{ color: 'var(--theme-info-text)' }}>
              <p className="font-semibold mb-2">About Session Management</p>
              <ul className="list-disc list-outside ml-4 space-y-1.5">
                <li className="pl-1">Only authenticated users (with API key) can access this user management panel</li>
                <li className="pl-1"><strong>Authenticated Users</strong> - Multiple users can share the same API key (up to configured device limit)</li>
                <li className="pl-1"><strong>Authenticated</strong> sessions have registered with the API key and don't expire</li>
                <li className="pl-1"><strong>Guest</strong> sessions have temporary 6-hour access with read-only permissions</li>
                <li className="pl-1"><strong>Revoke</strong> - Immediately kicks out guest users (marks them as revoked)</li>
                <li className="pl-1"><strong>Delete</strong> - Permanently removes the session record from history</li>
                <li className="pl-1">Revoked guests will see an "expired" message on their next request</li>
              </ul>
            </div>
          </div>
        </div>
      </Card>

      {/* Revoke Session Modal */}
      <Modal
        opened={showRevokeModal}
        onClose={() => {
          if (!revokingSession) {
            setShowRevokeModal(false);
            setPendingRevokeSession(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Revoke Session</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to revoke this {pendingRevokeSession?.type === 'authenticated' ? 'authenticated user' : 'guest'}?
          </p>

          {pendingRevokeSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {pendingRevokeSession.deviceName || 'Unknown Device'}
              </p>
              {pendingRevokeSession.type === 'authenticated' ? (
                // Authenticated: Show combined Device/Session ID
                <p className="text-xs text-themed-muted font-mono">
                  Device/Session ID: {pendingRevokeSession.id}
                </p>
              ) : (
                // Guest: Show both IDs separately
                <>
                  {pendingRevokeSession.deviceId && (
                    <p className="text-xs text-themed-muted font-mono">
                      Device ID: {pendingRevokeSession.deviceId}
                    </p>
                  )}
                  <p className="text-xs text-themed-muted font-mono">
                    Session ID: {pendingRevokeSession.id}
                  </p>
                </>
              )}
            </div>
          )}

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">What happens when you revoke:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>The session is marked as revoked but not deleted</li>
                <li>The user will be logged out immediately</li>
                <li>The session record remains in history</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => {
                setShowRevokeModal(false);
                setPendingRevokeSession(null);
              }}
              disabled={!!revokingSession}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="orange"
              onClick={confirmRevokeSession}
              loading={!!revokingSession}
            >
              Revoke Session
            </Button>
          </div>
        </div>
      </Modal>

      {/* Delete Session Modal */}
      <Modal
        opened={showDeleteModal}
        onClose={() => {
          if (!deletingSession) {
            setShowDeleteModal(false);
            setPendingDeleteSession(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Trash2 className="w-6 h-6 text-themed-error" />
            <span>Delete Session</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to permanently delete this {pendingDeleteSession?.type === 'authenticated' ? 'authenticated device' : 'guest session'}?
          </p>

          {pendingDeleteSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {pendingDeleteSession.deviceName || 'Unknown Device'}
              </p>
              {pendingDeleteSession.type === 'authenticated' ? (
                // Authenticated: Show combined Device/Session ID
                <p className="text-xs text-themed-muted font-mono">
                  Device/Session ID: {pendingDeleteSession.id}
                </p>
              ) : (
                // Guest: Show both IDs separately
                <>
                  {pendingDeleteSession.deviceId && (
                    <p className="text-xs text-themed-muted font-mono">
                      Device ID: {pendingDeleteSession.deviceId}
                    </p>
                  )}
                  <p className="text-xs text-themed-muted font-mono">
                    Session ID: {pendingDeleteSession.id}
                  </p>
                </>
              )}
            </div>
          )}

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">Warning:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>The session will be permanently removed from history</li>
                <li>The user will be logged out immediately</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => {
                setShowDeleteModal(false);
                setPendingDeleteSession(null);
              }}
              disabled={!!deletingSession}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<Trash2 className="w-4 h-4" />}
              onClick={confirmDeleteSession}
              loading={!!deletingSession}
            >
              Delete Permanently
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default UserTab;
