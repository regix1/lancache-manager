import React, { useEffect, useState } from 'react';
import { Shield, Trash2, RefreshCw, AlertTriangle, Clock, User, Users } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import ApiService from '@services/api.service';

interface Session {
  id: string;
  deviceName: string | null;
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

const AdminTab: React.FC = () => {
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
    <div className="container mx-auto px-4 py-6 space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="p-2 rounded-lg" style={{ backgroundColor: 'var(--theme-primary-subtle)' }}>
            <Shield className="w-6 h-6" style={{ color: 'var(--theme-primary)' }} />
          </div>
          <div>
            <h1 className="text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>Admin Panel</h1>
            <p className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
              Manage all users and sessions • Live refresh
            </p>
          </div>
        </div>
        <Button
          variant="default"
          leftSection={<RefreshCw className="w-4 h-4" />}
          onClick={() => loadSessions(true)}
          disabled={loading}
        >
          Refresh Now
        </Button>
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
              <Shield className="w-8 h-8" style={{ color: 'var(--theme-success)' }} />
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
              <User className="w-8 h-8" style={{ color: 'var(--theme-info)' }} />
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
              <RefreshCw
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
                  className="p-4 rounded-lg"
                  style={{
                    backgroundColor: (session.isExpired || session.isRevoked) ? 'var(--theme-bg-tertiary)' : 'var(--theme-bg-secondary)',
                    border: '1px solid var(--theme-border)',
                    opacity: (session.isExpired || session.isRevoked) ? 0.6 : 1
                  }}
                >
                  <div className="flex items-start justify-between gap-4">
                    <div className="flex items-start gap-3 flex-1 min-w-0">
                      <div
                        className="mt-1 p-2 rounded-lg"
                        style={{
                          backgroundColor: session.type === 'authenticated'
                            ? 'var(--theme-primary-subtle)'
                            : 'var(--theme-info-bg)'
                        }}
                      >
                        <User
                          className="w-5 h-5"
                          style={{
                            color: session.type === 'authenticated'
                              ? 'var(--theme-primary)'
                              : 'var(--theme-info)'
                          }}
                        />
                      </div>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center gap-2 mb-2 flex-wrap">
                          <h3 className="font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
                            {session.deviceName || 'Unknown Device'}
                          </h3>
                          {session.type === 'authenticated' && (
                            <span
                              className="px-2 py-0.5 text-xs rounded font-medium"
                              style={{
                                backgroundColor: 'var(--theme-warning-bg)',
                                color: 'var(--theme-warning-text)',
                                border: '1px solid var(--theme-warning)'
                              }}
                            >
                              ADMIN
                            </span>
                          )}
                          {session.type === 'guest' && (
                            <span
                              className="px-2 py-0.5 text-xs rounded font-medium"
                              style={{
                                backgroundColor: 'var(--theme-info-bg)',
                                color: 'var(--theme-info-text)'
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

                        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-x-6 gap-y-1 text-sm">
                          {session.hostname && (
                            <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                              <User className="w-4 h-4" />
                              <span>{session.hostname}</span>
                            </div>
                          )}
                          {session.operatingSystem && (
                            <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                              <Users className="w-4 h-4" />
                              <span>{session.operatingSystem}</span>
                            </div>
                          )}
                          {session.browser && (
                            <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                              <Users className="w-4 h-4" />
                              <span>{session.browser}</span>
                            </div>
                          )}
                          <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                            <Clock className="w-4 h-4" />
                            <span>Created: {formatDate(session.createdAt)}</span>
                          </div>
                          {session.lastSeenAt && (
                            <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                              <Clock className="w-4 h-4" />
                              <span>Last seen: {formatDate(session.lastSeenAt)}</span>
                            </div>
                          )}
                          {session.revokedAt && session.type === 'guest' && (
                            <div className="flex items-center gap-2" style={{ color: 'var(--theme-error-text)' }}>
                              <Clock className="w-4 h-4" />
                              <span>Revoked: {formatDate(session.revokedAt)}</span>
                            </div>
                          )}
                          {session.revokedBy && session.type === 'guest' && (
                            <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                              <Users className="w-4 h-4" />
                              <span>Revoked by: {session.revokedBy}</span>
                            </div>
                          )}
                        </div>

                        <div
                          className="mt-2 text-xs font-mono truncate"
                          style={{ color: 'var(--theme-text-muted)' }}
                        >
                          ID: {session.id}
                        </div>
                      </div>
                    </div>

                    <div className="flex gap-2">
                      {session.type === 'guest' && !session.isRevoked && (
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
      <Card>
        <div
          className="p-4 border-l-4"
          style={{
            backgroundColor: 'var(--theme-info-bg)',
            borderLeftColor: 'var(--theme-info)'
          }}
        >
          <div className="flex gap-3">
            <AlertTriangle className="w-5 h-5 flex-shrink-0" style={{ color: 'var(--theme-info)' }} />
            <div className="text-sm" style={{ color: 'var(--theme-info-text)' }}>
              <p className="font-semibold mb-1">About Session Management</p>
              <ul className="list-disc list-inside space-y-1">
                <li>Only authenticated users (with API key) can access this admin panel</li>
                <li><strong>Admin Users</strong> - Multiple admins can share the same API key (up to configured device limit)</li>
                <li><strong>Authenticated</strong> sessions have registered with the API key and don't expire</li>
                <li><strong>Guest</strong> sessions have temporary 6-hour access with read-only permissions</li>
                <li><strong>Revoke</strong> - Immediately kicks out guest users (marks them as revoked)</li>
                <li><strong>Delete</strong> - Permanently removes the session record from history</li>
                <li>Revoked guests will see an "expired" message on their next request</li>
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
            <div className="p-3 rounded-lg bg-themed-tertiary">
              <p className="text-sm text-themed-primary font-medium mb-1">
                {pendingRevokeSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                {pendingRevokeSession.id}
              </p>
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
            <div className="p-3 rounded-lg bg-themed-tertiary">
              <p className="text-sm text-themed-primary font-medium mb-1">
                {pendingDeleteSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                {pendingDeleteSession.id}
              </p>
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

export default AdminTab;
