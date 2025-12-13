import React, { useEffect, useState, useCallback } from 'react';
import {
  Users,
  User,
  Trash2,
  RefreshCw,
  Loader2,
  AlertTriangle,
  Clock,
  Network,
  Monitor,
  Globe,
  Edit
} from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { HelpPopover } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Pagination } from '@components/ui/Pagination';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import authService from '@services/auth.service';
import { getErrorMessage } from '@utils/error';
import { useAuth } from '@contexts/AuthContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useSignalR } from '@contexts/SignalRContext';

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

/** User preferences for theme and UI settings */
interface UserPreferences {
  selectedTheme: string | null;
  sharpCorners: boolean;
  disableFocusOutlines: boolean;
  disableTooltips: boolean;
  picsAlwaysVisible: boolean;
  disableStickyNotifications: boolean;
  showDatasourceLabels: boolean;
}

// Helper component for session timestamps with timezone awareness
const SessionTimestamp: React.FC<{
  label: string;
  timestamp: string;
  color?: string;
}> = ({ label, timestamp, color = 'var(--theme-text-secondary)' }) => {
  const formattedTime = useFormattedDateTime(timestamp);
  return (
    <div className="flex items-center gap-2" style={{ color }}>
      <Clock className="w-4 h-4 flex-shrink-0" />
      <span className="truncate" title={`${label}: ${formattedTime}`}>
        {label}: {formattedTime}
      </span>
    </div>
  );
};

const UserTab: React.FC = () => {
  const { refreshAuth } = useAuth();
  const { on, off } = useSignalR();
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [revokingSession, setRevokingSession] = useState<string | null>(null);

  // Helper to show toast notifications
  const showToast = (type: 'success' | 'error' | 'info', message: string) => {
    window.dispatchEvent(new CustomEvent('show-toast', {
      detail: { type, message, duration: 4000 }
    }));
  };
  const [deletingSession, setDeletingSession] = useState<string | null>(null);
  const [pendingRevokeSession, setPendingRevokeSession] = useState<Session | null>(null);
  const [pendingDeleteSession, setPendingDeleteSession] = useState<Session | null>(null);
  const [guestDurationHours, setGuestDurationHours] = useState<number>(6);
  const [updatingDuration, setUpdatingDuration] = useState(false);
  const [defaultGuestTheme, setDefaultGuestTheme] = useState<string>('dark-default');
  const [updatingGuestTheme, setUpdatingGuestTheme] = useState(false);
  const [availableThemes, setAvailableThemes] = useState<{ id: string; name: string }[]>([]);
  const [editingSession, setEditingSession] = useState<Session | null>(null);
  const [editingPreferences, setEditingPreferences] = useState<UserPreferences | null>(null);
  const [loadingPreferences, setLoadingPreferences] = useState(false);
  const [savingPreferences, setSavingPreferences] = useState(false);
  const [currentPage, setCurrentPage] = useState(1);
  const [pageSize] = useState(20);
  const [totalPages, setTotalPages] = useState(1);
  const [totalCount, setTotalCount] = useState(0);

  const loadSessions = useCallback(async (showLoading = false, page = currentPage) => {
    try {
      if (showLoading) {
        setLoading(true);
      }
      const response = await fetch(`/api/sessions?page=${page}&pageSize=${pageSize}`, {
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        const data = await response.json();

        // Debug: Check for duplicate sessions
        const sessions = data.sessions || [];
        const deviceIds = sessions.map((s: Session) => s.deviceId || s.id);
        const uniqueDeviceIds = new Set(deviceIds);
        if (deviceIds.length !== uniqueDeviceIds.size) {
          console.warn('[UserTab] Duplicate sessions detected in API response:', sessions);
          console.warn('[UserTab] Device IDs:', deviceIds);
        }

        setSessions(sessions);
        setTotalPages(data.pagination?.totalPages || 1);
        setTotalCount(data.pagination?.totalCount || sessions.length);
        setCurrentPage(data.pagination?.page || 1);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to load sessions');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to load sessions');
    } finally {
      if (showLoading) {
        setLoading(false);
      }
    }
  }, [currentPage, pageSize]);

  const loadGuestDuration = async () => {
    try {
      const response = await fetch('/api/auth/guest/config', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const data = await response.json();
        setGuestDurationHours(data.durationHours || 6);
      } else {
        // Fallback to default if endpoint fails
        setGuestDurationHours(6);
      }
    } catch (err) {
      console.error('Failed to load guest duration:', err);
      // Fallback to default on error
      setGuestDurationHours(6);
    }
  };

  const handleUpdateDuration = async (newDuration: number) => {
    try {
      setUpdatingDuration(true);
      await ApiService.setGuestSessionDuration(newDuration);
      setGuestDurationHours(newDuration);
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to update guest session duration');
    } finally {
      setUpdatingDuration(false);
    }
  };

  const loadAvailableThemes = async () => {
    try {
      const themes = await themeService.loadThemes();
      setAvailableThemes(
        themes.map((theme) => ({
          id: theme.meta.id,
          name: theme.meta.name
        }))
      );
    } catch (err) {
      console.error('Failed to load available themes:', err);
    }
  };

  const loadDefaultGuestTheme = async () => {
    try {
      const response = await fetch('/api/themes/preferences/guest', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const data = await response.json();
        setDefaultGuestTheme(data.themeId || 'dark-default');
      }
    } catch (err) {
      console.error('Failed to load default guest theme:', err);
    }
  };

  const handleUpdateGuestTheme = async (newThemeId: string) => {
    try {
      setUpdatingGuestTheme(true);
      const response = await fetch('/api/themes/preferences/guest', {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json',
          ...ApiService.getHeaders()
        },
        body: JSON.stringify({ themeId: newThemeId })
      });

      if (response.ok) {
        setDefaultGuestTheme(newThemeId);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to update default guest theme');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to update default guest theme');
    } finally {
      setUpdatingGuestTheme(false);
    }
  };

  const durationOptions = [
    { value: '1', label: '1 hour' },
    { value: '2', label: '2 hours' },
    { value: '3', label: '3 hours' },
    { value: '6', label: '6 hours' },
    { value: '12', label: '12 hours' },
    { value: '24', label: '24 hours (1 day)' },
    { value: '48', label: '48 hours (2 days)' },
    { value: '72', label: '72 hours (3 days)' },
    { value: '168', label: '168 hours (1 week)' }
  ];

  // SignalR handler for session changes
  const handleSessionRevoked = useCallback(() => {
    // Refresh sessions list when a session is revoked/deleted via SignalR
    loadSessions(false);
  }, [loadSessions]);

  // SignalR handler for sessions cleared (e.g., during database reset)
  const handleSessionsCleared = useCallback(() => {
    // Refresh sessions list when all sessions are cleared
    loadSessions(false);
  }, [loadSessions]);

  // SignalR handler for new session created
  const handleSessionCreated = useCallback(() => {
    // Refresh sessions list when a new session is created via SignalR
    loadSessions(false);
  }, [loadSessions]);

  useEffect(() => {
    // Initial load with loading spinner
    loadSessions(true);
    loadGuestDuration();
    loadAvailableThemes();
    loadDefaultGuestTheme();

    // Subscribe to SignalR events for real-time session updates
    on('UserSessionRevoked', handleSessionRevoked);
    on('UserSessionsCleared', handleSessionsCleared);
    on('UserSessionCreated', handleSessionCreated);

    // Cleanup SignalR subscriptions on unmount
    return () => {
      off('UserSessionRevoked', handleSessionRevoked);
      off('UserSessionsCleared', handleSessionsCleared);
      off('UserSessionCreated', handleSessionCreated);
    };
  }, [loadSessions, on, off, handleSessionRevoked, handleSessionsCleared, handleSessionCreated]);

  const handleRevokeSession = (session: Session) => {
    setPendingRevokeSession(session);
  };

  const confirmRevokeSession = async () => {
    if (!pendingRevokeSession) return;

    // Check if we're about to revoke our own session
    const isOwnSession =
      (pendingRevokeSession.type === 'authenticated' &&
        pendingRevokeSession.id === authService.getDeviceId()) ||
      (pendingRevokeSession.type === 'guest' &&
        pendingRevokeSession.id === authService.getGuestSessionId());

    try {
      setRevokingSession(pendingRevokeSession.id);
      // RESTful endpoint: DELETE /api/sessions/{id}?action=revoke
      // For guests: revokes (marks as revoked but keeps in history)
      // For authenticated: always deletes (no revoke-only option)
      const endpoint = `/api/sessions/${encodeURIComponent(pendingRevokeSession.id)}?action=revoke`;

      const response = await fetch(endpoint, {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        // If we just revoked our own session, show feedback then logout
        if (isOwnSession) {
          console.warn('[UserTab] You revoked your own session - forcing logout');
          setPendingRevokeSession(null);
          showToast('info', 'You revoked your own session. Logging out...');

          // Wait 2 seconds so user can see the message
          setTimeout(async () => {
            authService.clearAuth(); // Clear local state without API call (session already revoked)
            await refreshAuth(); // Refresh auth state to show authentication modal
          }, 2000);
          return;
        }

        await loadSessions(false);
        setPendingRevokeSession(null);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.message || errorData.error || 'Failed to revoke session');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to revoke session');
    } finally {
      setRevokingSession(null);
    }
  };

  const handleDeleteSession = (session: Session) => {
    setPendingDeleteSession(session);
  };

  const confirmDeleteSession = async () => {
    if (!pendingDeleteSession) return;

    // Check if we're about to delete our own session
    const isOwnSession =
      (pendingDeleteSession.type === 'authenticated' &&
        pendingDeleteSession.id === authService.getDeviceId()) ||
      (pendingDeleteSession.type === 'guest' &&
        pendingDeleteSession.id === authService.getGuestSessionId());

    try {
      setDeletingSession(pendingDeleteSession.id);
      // RESTful endpoint: DELETE /api/sessions/{id}?action=delete
      // Permanently removes the session from the database
      const endpoint = `/api/sessions/${encodeURIComponent(pendingDeleteSession.id)}?action=delete`;

      const response = await fetch(endpoint, {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        // If we just deleted our own session, show feedback then logout
        if (isOwnSession) {
          console.warn('[UserTab] You deleted your own session - forcing logout');
          setPendingDeleteSession(null);
          showToast('info', 'You deleted your own session. Logging out...');

          // Wait 2 seconds so user can see the message
          setTimeout(async () => {
            authService.clearAuth(); // Clear local state without API call (session already deleted)
            await refreshAuth(); // Refresh auth state to show authentication modal
          }, 2000);
          return;
        }

        await loadSessions(false);
        setPendingDeleteSession(null);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.message || errorData.error || 'Failed to delete session');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to delete session');
    } finally {
      setDeletingSession(null);
    }
  };

  const handleEditSession = async (session: Session) => {
    setEditingSession(session);
    setLoadingPreferences(true);
    try {
      const response = await fetch(
        `/api/user-preferences/session/${encodeURIComponent(session.id)}`,
        {
          headers: ApiService.getHeaders()
        }
      );

      if (response.ok) {
        const prefs = await response.json();
        // Ensure all boolean fields have proper defaults
        // Convert empty string or undefined to null for selectedTheme
        const selectedTheme =
          prefs.selectedTheme && prefs.selectedTheme.trim() !== '' ? prefs.selectedTheme : null;
        setEditingPreferences({
          selectedTheme: selectedTheme,
          sharpCorners: prefs.sharpCorners ?? false,
          disableFocusOutlines: prefs.disableFocusOutlines ?? true,
          disableTooltips: prefs.disableTooltips ?? false,
          picsAlwaysVisible: prefs.picsAlwaysVisible ?? false,
          disableStickyNotifications: prefs.disableStickyNotifications ?? false,
          showDatasourceLabels: prefs.showDatasourceLabels ?? true
        });
      } else {
        // Initialize with defaults if no preferences exist
        setEditingPreferences({
          selectedTheme: null,
          sharpCorners: false,
          disableFocusOutlines: true,
          disableTooltips: false,
          picsAlwaysVisible: false,
          disableStickyNotifications: false,
          showDatasourceLabels: true
        });
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to load user preferences');
      setEditingSession(null);
    } finally {
      setLoadingPreferences(false);
    }
  };

  const handleSavePreferences = async () => {
    if (!editingSession || !editingPreferences) return;

    try {
      setSavingPreferences(true);
      const response = await fetch(
        `/api/user-preferences/session/${encodeURIComponent(editingSession.id)}`,
        {
          method: 'PUT',
          headers: {
            'Content-Type': 'application/json',
            ...ApiService.getHeaders()
          },
          body: JSON.stringify(editingPreferences)
        }
      );

      if (response.ok) {
        // Check if we're editing our own session
        const isOwnSession =
          (editingSession.type === 'authenticated' &&
            editingSession.id === authService.getDeviceId()) ||
          (editingSession.type === 'guest' &&
            editingSession.id === authService.getGuestSessionId());

        // If editing own session, apply the changes immediately
        if (isOwnSession) {
          if (editingPreferences.selectedTheme) {
            await themeService.setTheme(editingPreferences.selectedTheme);
          }
          await themeService.setSharpCorners(editingPreferences.sharpCorners);
          await themeService.setDisableTooltips(editingPreferences.disableTooltips);
          await themeService.setDisableStickyNotifications(
            editingPreferences.disableStickyNotifications
          );
          await themeService.setPicsAlwaysVisible(editingPreferences.picsAlwaysVisible);
        }

        setEditingSession(null);
        setEditingPreferences(null);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to save preferences');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to save preferences');
    } finally {
      setSavingPreferences(false);
    }
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

  const isSessionActive = (session: Session) => {
    // Don't show active for revoked or expired sessions
    if (session.isRevoked || session.isExpired) return false;

    // Check if lastSeenAt is within the last 60 seconds (1 minute)
    if (!session.lastSeenAt) return false;

    const now = new Date();
    const lastSeen = new Date(session.lastSeenAt);
    const diffSeconds = (now.getTime() - lastSeen.getTime()) / 1000;

    return diffSeconds <= 60;
  };

  return (
    <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6 space-y-4 sm:space-y-6">
      {/* Header */}
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-center gap-3 min-w-0 flex-1">
          <div
            className="p-2 rounded-lg flex-shrink-0"
            style={{ backgroundColor: 'var(--theme-primary-subtle)' }}
          >
            <Users className="w-6 h-6" style={{ color: 'var(--theme-primary)' }} />
          </div>
          <div className="min-w-0">
            <h1
              className="text-xl sm:text-2xl font-bold"
              style={{ color: 'var(--theme-text-primary)' }}
            >
              User Management
            </h1>
            <p className="text-xs sm:text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
              Manage all users and devices • Real-time updates
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
          title="Refresh devices"
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
                <p className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                  Total Users
                </p>
                <p className="text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>
                  {sessions.length}
                </p>
              </div>
              <Users className="w-8 h-8" style={{ color: 'var(--theme-primary)' }} />
            </div>
          </div>
        </Card>
        <Card>
          <div className="p-4">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                  Authenticated
                </p>
                <p className="text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>
                  {sessions.filter((s) => s.type === 'authenticated').length}
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
                <p className="text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                  Guests
                </p>
                <p className="text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>
                  {sessions.filter((s) => s.type === 'guest').length}
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
          <div className="flex items-center gap-2 mb-4">
            <h2 className="text-lg font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
              All Sessions
            </h2>
            <HelpPopover
              width={300}
              sections={[
                {
                  title: 'Session Types',
                  items: [
                    { label: 'Authenticated', description: 'Full access, no expiration', color: 'var(--theme-user-session)' },
                    { label: 'Guest', description: `Read-only for ${guestDurationHours} hours`, color: 'var(--theme-guest-session)' }
                  ]
                },
                {
                  title: 'Actions',
                  items: [
                    { label: 'Revoke', description: 'End a guest session immediately', color: 'var(--theme-warning)' },
                    { label: 'Delete', description: 'Remove device from history', color: 'var(--theme-error)' }
                  ]
                }
              ]}
            />
          </div>

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

          {!loading && sessions.length === 0 && (
            <div className="text-center py-8">
              <Users
                className="w-12 h-12 mx-auto mb-2"
                style={{ color: 'var(--theme-text-muted)' }}
              />
              <p style={{ color: 'var(--theme-text-secondary)' }}>No active sessions</p>
            </div>
          )}

          {!loading && sessions.length > 0 && (
            <div className="space-y-3">
              {sessions.map((session) => (
                <div
                  key={session.id}
                  className="p-3 sm:p-4 rounded-lg"
                  style={{
                    backgroundColor:
                      session.isExpired || session.isRevoked
                        ? 'var(--theme-bg-tertiary)'
                        : 'var(--theme-bg-secondary)',
                    border: '1px solid var(--theme-border)'
                  }}
                >
                  <div className="flex flex-col sm:flex-row items-start justify-between gap-4">
                    <div
                      className="flex items-center gap-3 flex-1 min-w-0 w-full sm:w-auto"
                      style={{
                        opacity: session.isExpired || session.isRevoked ? 0.6 : 1
                      }}
                    >
                      <div
                        className="p-2 rounded-lg flex-shrink-0"
                        style={{
                          backgroundColor:
                            session.type === 'authenticated'
                              ? 'var(--theme-user-session-bg)'
                              : 'var(--theme-guest-session-bg)'
                        }}
                      >
                        <User
                          className="w-5 h-5"
                          style={{
                            color:
                              session.type === 'authenticated'
                                ? 'var(--theme-user-session)'
                                : 'var(--theme-guest-session)'
                          }}
                        />
                      </div>
                      <div className="flex-1 min-w-0">
                        <div className="grid gap-2">
                          <div className="flex items-center gap-2 flex-wrap">
                            <h3
                              className="font-semibold truncate"
                              style={{ color: 'var(--theme-text-primary)' }}
                            >
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
                            {isSessionActive(session) && (
                              <span
                                className="px-2 py-0.5 text-xs rounded font-medium"
                                style={{
                                  backgroundColor: 'var(--theme-active-session-bg)',
                                  color: 'var(--theme-active-session)'
                                }}
                              >
                                Active
                              </span>
                            )}
                            {session.type === 'guest' &&
                              !session.isRevoked &&
                              !session.isExpired && (
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
                              <div
                                className="flex items-center gap-2"
                                style={{ color: 'var(--theme-text-secondary)' }}
                              >
                                <Network className="w-4 h-4 flex-shrink-0" />
                                <span
                                  className="truncate"
                                  title={
                                    session.localIp ? `Local IP: ${session.localIp}` : undefined
                                  }
                                >
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
                              <div
                                className="flex items-center gap-2"
                                style={{ color: 'var(--theme-text-secondary)' }}
                              >
                                <Monitor className="w-4 h-4 flex-shrink-0" />
                                <span className="truncate" title={session.operatingSystem}>
                                  {session.operatingSystem}
                                </span>
                              </div>
                            )}

                            {/* Browser */}
                            {session.browser && (
                              <div
                                className="flex items-center gap-2"
                                style={{ color: 'var(--theme-text-secondary)' }}
                              >
                                <Globe className="w-4 h-4 flex-shrink-0" />
                                <span className="truncate" title={session.browser}>
                                  {session.browser}
                                </span>
                              </div>
                            )}

                            {/* Created */}
                            <SessionTimestamp label="Created" timestamp={session.createdAt} />

                            {/* Last seen */}
                            {session.lastSeenAt && (
                              <SessionTimestamp label="Last seen" timestamp={session.lastSeenAt} />
                            )}

                            {/* Revoked (guests only) */}
                            {session.revokedAt && session.type === 'guest' && (
                              <SessionTimestamp
                                label="Revoked"
                                timestamp={session.revokedAt}
                                color="var(--theme-error-text)"
                              />
                            )}

                            {/* Revoked by (guests only) */}
                            {session.revokedBy && session.type === 'guest' && (
                              <div
                                className="flex items-center gap-2"
                                style={{ color: 'var(--theme-text-secondary)' }}
                              >
                                <User className="w-4 h-4 flex-shrink-0" />
                                <span
                                  className="truncate"
                                  title={`Revoked by: ${(() => {
                                    const cleanIp = session.revokedBy.replace('::ffff:', '');
                                    if (cleanIp === '::1' || cleanIp === '127.0.0.1') {
                                      return 'localhost';
                                    }
                                    return cleanIp;
                                  })()}`}
                                >
                                  Revoked by:{' '}
                                  {(() => {
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

                          {/* Device ID Display */}
                          <div
                            className="text-xs font-mono truncate overflow-x-auto"
                            style={{ color: 'var(--theme-text-muted)' }}
                            title={`Device ID: ${session.deviceId || session.id}`}
                          >
                            Device ID: {session.deviceId || session.id}
                          </div>
                        </div>
                      </div>
                    </div>

                    <div className="flex gap-2 items-start justify-end w-full sm:w-auto sm:min-w-[240px]">
                      <Button
                        variant="default"
                        color="blue"
                        size="sm"
                        leftSection={<Edit className="w-4 h-4" />}
                        onClick={() => handleEditSession(session)}
                      >
                        Edit
                      </Button>
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
                        style={
                          session.isExpired || session.isRevoked
                            ? {
                                backgroundColor: 'var(--theme-bg-secondary)',
                                borderColor: 'var(--theme-error)'
                              }
                            : undefined
                        }
                      >
                        {deletingSession === session.id ? 'Deleting...' : 'Delete'}
                      </Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}

          {/* Pagination */}
          {!loading && (
            <div className="mt-6 border-t pt-4" style={{ borderColor: 'var(--theme-border)' }}>
              <Pagination
                currentPage={currentPage}
                totalPages={totalPages}
                totalItems={totalCount}
                itemsPerPage={pageSize}
                onPageChange={(newPage) => {
                  setCurrentPage(newPage);
                  loadSessions(true, newPage);
                }}
                itemLabel="sessions"
                showCard={false}
              />
            </div>
          )}
        </div>
      </Card>

      {/* Guest Device Configuration */}
      <Card>
        <div className="p-6">
          <h3
            className="text-lg font-semibold mb-4 flex items-center gap-2"
            style={{ color: 'var(--theme-text-primary)' }}
          >
            <Clock className="w-5 h-5" />
            Guest Device Configuration
          </h3>
          <div className="space-y-4">
            <div>
              <label
                className="block text-sm font-medium mb-2"
                style={{ color: 'var(--theme-text-primary)' }}
              >
                Guest Device Duration
              </label>
              <div className="flex items-center gap-3">
                <EnhancedDropdown
                  options={durationOptions}
                  value={guestDurationHours.toString()}
                  onChange={(value) => handleUpdateDuration(Number(value))}
                  disabled={updatingDuration}
                  className="w-64"
                />
                {updatingDuration && (
                  <Loader2
                    className="w-4 h-4 animate-spin"
                    style={{ color: 'var(--theme-primary)' }}
                  />
                )}
              </div>
              <p className="text-xs mt-2" style={{ color: 'var(--theme-text-muted)' }}>
                How long guest devices remain valid before expiring
              </p>
            </div>

            <div>
              <label
                className="block text-sm font-medium mb-2"
                style={{ color: 'var(--theme-text-primary)' }}
              >
                Default Guest Theme
              </label>
              <div className="flex items-center gap-3">
                <EnhancedDropdown
                  options={availableThemes.map((theme) => ({
                    value: theme.id,
                    label: theme.name
                  }))}
                  value={defaultGuestTheme}
                  onChange={handleUpdateGuestTheme}
                  disabled={updatingGuestTheme}
                  className="w-64"
                />
                {updatingGuestTheme && (
                  <Loader2
                    className="w-4 h-4 animate-spin"
                    style={{ color: 'var(--theme-primary)' }}
                  />
                )}
              </div>
              <p className="text-xs mt-2" style={{ color: 'var(--theme-text-muted)' }}>
                Default theme applied to all guest users (guests cannot change their theme)
              </p>
            </div>
          </div>
        </div>
      </Card>

      {/* Revoke Device Modal */}
      <Modal
        opened={!!pendingRevokeSession}
        onClose={() => {
          if (!revokingSession) {
            setPendingRevokeSession(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Revoke Device</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to revoke this{' '}
            {pendingRevokeSession?.type === 'authenticated' ? 'authenticated user' : 'guest'}?
          </p>

          {pendingRevokeSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {pendingRevokeSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                Device ID: {pendingRevokeSession.id}
              </p>
            </div>
          )}

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">What happens when you revoke:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>The device is marked as revoked but not deleted</li>
                <li>The user will be logged out immediately</li>
                <li>The device record remains in history</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingRevokeSession(null)}
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
              Revoke Device
            </Button>
          </div>
        </div>
      </Modal>

      {/* Delete Device Modal */}
      <Modal
        opened={!!pendingDeleteSession}
        onClose={() => {
          if (!deletingSession) {
            setPendingDeleteSession(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Trash2 className="w-6 h-6 text-themed-error" />
            <span>Delete Device</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to permanently delete this{' '}
            {pendingDeleteSession?.type === 'authenticated'
              ? 'authenticated device'
              : 'guest device'}
            ?
          </p>

          {pendingDeleteSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {pendingDeleteSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                Device ID: {pendingDeleteSession.id}
              </p>
            </div>
          )}

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">Warning:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>The device will be permanently removed from history</li>
                <li>The user will be logged out immediately</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingDeleteSession(null)}
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

      {/* Edit User Preferences Modal */}
      <Modal
        opened={!!editingSession}
        onClose={() => {
          if (!savingPreferences) {
            setEditingSession(null);
            setEditingPreferences(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Edit className="w-6 h-6 text-themed-accent" />
            <span>Edit User Preferences</span>
          </div>
        }
        size="lg"
      >
        <div className="space-y-4">
          {editingSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {editingSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted">
                {editingSession.type === 'authenticated' ? 'Authenticated User' : 'Guest User'}
              </p>
              <p className="text-xs text-themed-muted font-mono">Device ID: {editingSession.id}</p>
            </div>
          )}

          {loadingPreferences && (
            <div className="text-center py-8">
              <Loader2
                className="w-8 h-8 animate-spin mx-auto"
                style={{ color: 'var(--theme-text-muted)' }}
              />
              <p className="text-sm mt-2" style={{ color: 'var(--theme-text-secondary)' }}>
                Loading preferences...
              </p>
            </div>
          )}

          {!loadingPreferences && editingPreferences && (
            <div className="space-y-4">
              {/* Theme Selection */}
              <div>
                <label className="block text-sm font-medium text-themed-primary mb-2">
                  Selected Theme
                </label>
                <EnhancedDropdown
                  options={[
                    {
                      value: 'default',
                      label: `Default Theme (${availableThemes.find((t) => t.id === defaultGuestTheme)?.name || defaultGuestTheme})`
                    },
                    ...availableThemes.map((theme) => ({
                      value: theme.id,
                      label: theme.name
                    }))
                  ]}
                  value={
                    !editingPreferences.selectedTheme ? 'default' : editingPreferences.selectedTheme
                  }
                  onChange={(value) =>
                    setEditingPreferences({
                      ...editingPreferences,
                      selectedTheme: value === 'default' ? null : value
                    })
                  }
                  className="w-full"
                />
              </div>

              {/* UI Preferences */}
              <div className="space-y-3">
                <h4 className="text-sm font-medium text-themed-primary">UI Preferences</h4>

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingPreferences.sharpCorners}
                    onChange={(e) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        sharpCorners: e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded"
                    style={{ accentColor: 'var(--theme-primary)' }}
                  />
                  <span className="text-sm text-themed-secondary">Sharp Corners</span>
                </label>

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={!editingPreferences.disableTooltips}
                    onChange={(e) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        disableTooltips: !e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded"
                    style={{ accentColor: 'var(--theme-primary)' }}
                  />
                  <span className="text-sm text-themed-secondary">Tooltips</span>
                </label>

                {/* Only show notification preferences for authenticated users */}
                {editingSession && editingSession.type === 'authenticated' && (
                  <>
                    <label className="flex items-center gap-3 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={!editingPreferences.disableStickyNotifications}
                        onChange={(e) =>
                          setEditingPreferences({
                            ...editingPreferences,
                            disableStickyNotifications: !e.target.checked
                          })
                        }
                        className="w-4 h-4 rounded"
                        style={{ accentColor: 'var(--theme-primary)' }}
                      />
                      <div className="flex flex-col">
                        <span className="text-sm text-themed-secondary">Sticky Notifications</span>
                        <span className="text-xs text-themed-muted">
                          Keep notification bar fixed at top when scrolling
                        </span>
                      </div>
                    </label>

                    <label className="flex items-center gap-3 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={editingPreferences.picsAlwaysVisible}
                        onChange={(e) =>
                          setEditingPreferences({
                            ...editingPreferences,
                            picsAlwaysVisible: e.target.checked
                          })
                        }
                        className="w-4 h-4 rounded"
                        style={{ accentColor: 'var(--theme-primary)' }}
                      />
                      <div className="flex flex-col">
                        <span className="text-sm text-themed-secondary">Static Notifications</span>
                        <span className="text-xs text-themed-muted">
                          Require manual dismissal - won&apos;t auto-clear
                        </span>
                      </div>
                    </label>
                  </>
                )}

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingPreferences.showDatasourceLabels}
                    onChange={(e) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        showDatasourceLabels: e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded"
                    style={{ accentColor: 'var(--theme-primary)' }}
                  />
                  <div className="flex flex-col">
                    <span className="text-sm text-themed-secondary">Datasource Labels</span>
                    <span className="text-xs text-themed-muted">
                      Show datasource indicators on downloads (multi-datasource mode)
                    </span>
                  </div>
                </label>
              </div>
            </div>
          )}

          <div
            className="flex justify-end space-x-3 pt-4 border-t"
            style={{ borderColor: 'var(--theme-border-secondary)' }}
          >
            <Button
              variant="default"
              onClick={() => {
                setEditingSession(null);
                setEditingPreferences(null);
              }}
              disabled={savingPreferences}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="blue"
              onClick={handleSavePreferences}
              loading={savingPreferences}
              disabled={loadingPreferences}
            >
              Save Preferences
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default UserTab;
