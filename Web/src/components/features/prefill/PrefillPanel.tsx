import { useEffect, useRef, useCallback, useState } from 'react';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Card, CardContent } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { Modal } from '../../ui/Modal';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { usePrefillSteamAuth } from '@hooks/usePrefillSteamAuth';
import { ActivityLog, type LogEntryType } from './ActivityLog';
import { GameSelectionModal, type OwnedGame } from './GameSelectionModal';
import { NetworkStatusSection } from './NetworkStatusSection';
import type { NetworkDiagnostics } from '@services/api.service';
import { usePrefillContext } from '@contexts/PrefillContext';
import { SteamIcon } from '@components/ui/SteamIcon';
import authService from '@services/auth.service';
import { SIGNALR_BASE, API_BASE } from '@utils/constants';
import { formatSpeed } from '@utils/formatters';
import {
  Loader2,
  ScrollText,
  X,
  XCircle,
  Clock,
  AlertCircle,
  Play,
  Download,
  List,
  Trash2,
  RefreshCw,
  Gamepad2,
  TrendingUp,
  ShoppingCart,
  LogIn,
  CheckCircle2,
  Zap,
  Timer,
  Shield,
  Settings,
  Monitor,
  Cpu
} from 'lucide-react';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown, type MultiSelectOption } from '@components/ui/MultiSelectDropdown';

// Auth states from backend - matches SteamAuthState enum
type SteamAuthState =
  | 'NotAuthenticated'
  | 'CredentialsRequired'
  | 'TwoFactorRequired'
  | 'EmailCodeRequired'
  | 'Authenticated';

interface PrefillSessionDto {
  id: string;
  userId: string;
  containerId: string;
  containerName: string;
  status: string;
  createdAt: string;
  expiresAt: string;
  endedAt: string | null;
  timeRemainingSeconds: number;
  authState: SteamAuthState;
  networkDiagnostics?: NetworkDiagnostics;
}

interface PrefillPanelProps {
  onSessionEnd?: () => void;
}

type CommandType =
  | 'select-apps'
  | 'prefill'
  | 'prefill-all'
  | 'prefill-recent'
  | 'prefill-recent-purchased'
  | 'prefill-top'
  | 'prefill-force'
  | 'clear-temp';

interface CommandButton {
  id: CommandType;
  label: string;
  description: string;
  icon: React.ReactNode;
  variant?: 'default' | 'outline' | 'filled' | 'subtle';
  requiresLogin?: boolean;
  color?: 'blue' | 'green' | 'red' | 'yellow' | 'purple' | 'gray' | 'orange' | 'default';
}

// Grouped command buttons for better organization
// Note: ALL commands require login - nothing works without Steam auth
const SELECTION_COMMANDS: CommandButton[] = [
  {
    id: 'select-apps',
    label: 'Select Apps',
    description: 'Choose games to prefill',
    icon: <List className="h-4 w-4" />,
    variant: 'filled',
    color: 'blue'
  }
];

const PREFILL_COMMANDS: CommandButton[] = [
  {
    id: 'prefill',
    label: 'Prefill Selected',
    description: 'Download selected games',
    icon: <Download className="h-4 w-4" />,
    variant: 'filled',
    color: 'green'
  },
  {
    id: 'prefill-all',
    label: 'Prefill All',
    description: 'All owned games',
    icon: <Gamepad2 className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'prefill-recent',
    label: 'Recent Played',
    description: 'Last 2 weeks',
    icon: <Clock className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'prefill-recent-purchased',
    label: 'Recent Bought',
    description: 'Last 2 weeks',
    icon: <ShoppingCart className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'prefill-top',
    label: 'Top 50',
    description: 'Popular games',
    icon: <TrendingUp className="h-4 w-4" />,
    variant: 'outline'
  }
];

const UTILITY_COMMANDS: CommandButton[] = [
  {
    id: 'prefill-force',
    label: 'Force Download',
    description: 'Re-download all',
    icon: <RefreshCw className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'clear-temp',
    label: 'Clear Temp',
    description: 'Free disk space',
    icon: <Trash2 className="h-4 w-4" />,
    variant: 'outline',
    color: 'red'
  }
];

// Operating system options for prefill (multi-select)
const OS_OPTIONS: MultiSelectOption[] = [
  { value: 'windows', label: 'Windows', description: 'Windows game depots' },
  { value: 'linux', label: 'Linux', description: 'Native Linux depots' },
  { value: 'macos', label: 'macOS', description: 'macOS depots' }
];

// Max concurrency/thread options
const THREAD_OPTIONS: DropdownOption[] = [
  { value: 'default', label: 'Auto', description: 'Let daemon decide (recommended)' },
  { value: '1', label: '1 Thread', description: 'Minimal bandwidth usage' },
  { value: '2', label: '2 Threads', description: 'Low bandwidth usage' },
  { value: '4', label: '4 Threads', description: 'Moderate bandwidth' },
  { value: '8', label: '8 Threads', description: 'High bandwidth' },
  { value: '16', label: '16 Threads', description: 'Very high bandwidth' },
  { value: '32', label: '32 Threads', description: 'Maximum performance' }
];

export function PrefillPanel({ onSessionEnd }: PrefillPanelProps) {
  const hubConnection = useRef<HubConnection | null>(null);
  const initializationAttempted = useRef(false);
  const isCancelling = useRef(false);

  // Use context for log entries (persists across tab switches)
  const { logEntries, addLog, clearLogs, backgroundCompletion, setBackgroundCompletion, clearBackgroundCompletion } = usePrefillContext();

  // Track page visibility for background completion detection
  const isPageHiddenRef = useRef(document.hidden);
  const prefillDurationRef = useRef<number>(0);
  const isReceivingProgressRef = useRef(false); // Track if actively receiving progress updates

  const [session, setSession] = useState<PrefillSessionDto | null>(null);
  const [isConnecting, setIsConnecting] = useState(false);
  const [isCreating, setIsCreating] = useState(false);
  const [isExecuting, setIsExecuting] = useState(false);
  const [isInitializing, setIsInitializing] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [timeRemaining, setTimeRemaining] = useState<number>(0);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [isLoggedIn, setIsLoggedIn] = useState(false);

  // Game selection state
  const [ownedGames, setOwnedGames] = useState<OwnedGame[]>([]);
  const [selectedAppIds, setSelectedAppIds] = useState<number[]>([]);
  const [showGameSelection, setShowGameSelection] = useState(false);
  const [isLoadingGames, setIsLoadingGames] = useState(false);

  // Prefill settings state
  const [selectedOS, setSelectedOS] = useState<string[]>(['windows', 'linux', 'macos']);
  const [maxConcurrency, setMaxConcurrency] = useState<string>('default');

  // Confirmation dialog state for large prefill operations
  const [pendingConfirmCommand, setPendingConfirmCommand] = useState<CommandType | null>(null);
  const [estimatedSize, setEstimatedSize] = useState<{
    bytes: number;
    loading: boolean;
    error?: string;
    apps?: Array<{
      appId: number;
      name: string;
      downloadSize: number;
      isUnsupportedOs?: boolean;
      unavailableReason?: string;
    }>;
    message?: string;
  }>({
    bytes: 0,
    loading: false
  });

  // Prefill progress state
  const [prefillProgress, setPrefillProgress] = useState<{
    state: string;
    message?: string;
    currentAppId: number;
    currentAppName?: string;
    percentComplete: number;
    bytesDownloaded: number;
    totalBytes: number;
    bytesPerSecond: number;
    elapsedSeconds: number;
  } | null>(null);

  // Steam auth hook for container-based authentication
  const {
    state: authState,
    actions: authActions,
    trigger2FAPrompt,
    triggerEmailPrompt
  } = usePrefillSteamAuth({
    sessionId: session?.id ?? null,
    hubConnection: hubConnection.current,
    onSuccess: () => {
      setShowAuthModal(false);
    },
    onError: () => {
      // Keep modal open on error to allow retry
    },
    onDeviceConfirmationTimeout: () => {
      // Close modal and show End Session button
      setShowAuthModal(false);
    }
  });

  /**
   * Handle auth state changes from backend SignalR events
   */
  const handleAuthStateChanged = useCallback(
    (newState: SteamAuthState) => {
      switch (newState) {
        case 'Authenticated':
          setIsLoggedIn(true);
          setShowAuthModal(false);
          authActions.resetAuthForm();
          addLog('success', 'Successfully logged in to Steam');
          break;
        case 'CredentialsRequired':
          authActions.resetAuthForm();
          setShowAuthModal(true);
          addLog('auth', 'Steam credentials required');
          break;
        case 'TwoFactorRequired':
          trigger2FAPrompt();
          setShowAuthModal(true);
          addLog('auth', 'Two-factor authentication required');
          break;
        case 'EmailCodeRequired':
          triggerEmailPrompt();
          setShowAuthModal(true);
          addLog('auth', 'Email verification code required');
          break;
        case 'NotAuthenticated':
          setIsLoggedIn(false);
          break;
      }
    },
    [authActions, trigger2FAPrompt, triggerEmailPrompt, addLog]
  );

  // Timer for session countdown
  useEffect(() => {
    if (!session || session.status !== 'Active') return;

    const interval = setInterval(() => {
      const remaining = Math.max(
        0,
        Math.floor((new Date(session.expiresAt).getTime() - Date.now()) / 1000)
      );
      setTimeRemaining(remaining);

      if (remaining <= 0) {
        setError('Session expired');
        handleEndSession();
      }
    }, 1000);

    return () => clearInterval(interval);
  }, [session]);

  const formatTimeRemaining = useCallback((seconds: number): string => {
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const secs = seconds % 60;
    if (hours > 0) {
      return `${hours}:${minutes.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
    }
    return `${minutes}:${secs.toString().padStart(2, '0')}`;
  }, []);

  const formatBytes = useCallback((bytes: number): string => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  }, []);

  const connectToHub = useCallback(async (): Promise<HubConnection | null> => {
    const deviceId = authService.getDeviceId();
    if (!deviceId) {
      setError('Not authenticated');
      return null;
    }

    // Reuse existing connection if already connected
    if (hubConnection.current?.state === 'Connected') {
      return hubConnection.current;
    }

    // Stop any existing connection before creating a new one
    if (hubConnection.current) {
      try {
        await hubConnection.current.stop();
      } catch {
        // Ignore errors stopping old connection
      }
      hubConnection.current = null;
    }

    setIsConnecting(true);
    setError(null);

    try {
      const connection = new HubConnectionBuilder()
        .withUrl(`${SIGNALR_BASE}/prefill-daemon?deviceId=${encodeURIComponent(deviceId)}`)
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Information)
        .build();

      // Handle daemon output - parse and add to log
      connection.on('TerminalOutput', (_sessionId: string, output: string) => {
        // Parse output and determine log type
        const trimmed = output.trim();
        if (!trimmed) return;

        // Detect log type from content
        let type: LogEntryType = 'info';
        if (trimmed.includes('Error') || trimmed.includes('error') || trimmed.includes('failed')) {
          type = 'error';
        } else if (
          trimmed.includes('Success') ||
          trimmed.includes('Complete') ||
          trimmed.includes('Done')
        ) {
          type = 'success';
        } else if (trimmed.includes('Warning') || trimmed.includes('warn')) {
          type = 'warning';
        } else if (
          trimmed.includes('Download') ||
          trimmed.includes('Prefill') ||
          trimmed.includes('%')
        ) {
          type = 'download';
        }

        addLog(type, trimmed);
      });

      // Handle auth state changes from backend
      connection.on('AuthStateChanged', (_sessionId: string, newState: SteamAuthState) => {
        handleAuthStateChanged(newState);
      });

      // Handle session subscribed confirmation
      connection.on('SessionSubscribed', (sessionDto: PrefillSessionDto) => {
        setSession(sessionDto);
        setTimeRemaining(sessionDto.timeRemainingSeconds);
        // Initialize login state from session auth state
        setIsLoggedIn(sessionDto.authState === 'Authenticated');
      });

      // Handle session ended
      connection.on('SessionEnded', (_sessionId: string, reason: string) => {
        addLog('warning', `Session ended: ${reason}`);
        setSession(null);
        setIsExecuting(false);
        setIsLoggedIn(false);
        setPrefillProgress(null);
        onSessionEnd?.();
      });

      // Handle prefill progress updates
      connection.on(
        'PrefillProgress',
        (
          _sessionId: string,
          progress: {
            state: string;
            message?: string;
            currentAppId: number;
            currentAppName?: string;
            percentComplete: number;
            bytesDownloaded: number;
            totalBytes: number;
            bytesPerSecond: number;
            elapsedSeconds: number;
          }
        ) => {
          // Final states should reset the cancelling flag and clear progress
          const isFinalState =
            progress.state === 'completed' ||
            progress.state === 'failed' ||
            progress.state === 'cancelled' ||
            progress.state === 'idle';

          if (isFinalState) {
            isCancelling.current = false;
            isReceivingProgressRef.current = false;
            setPrefillProgress(null);
            return;
          }

          // Ignore progress updates while cancellation is in progress
          if (isCancelling.current) {
            return;
          }

          // Mark that we're actively receiving progress
          isReceivingProgressRef.current = true;

          if (progress.state === 'downloading') {
            setPrefillProgress(progress);
            // Track elapsed time for background completion message
            prefillDurationRef.current = progress.elapsedSeconds;
          } else if (
            progress.state === 'loading-metadata' ||
            progress.state === 'metadata-loaded' ||
            progress.state === 'starting' ||
            progress.state === 'preparing'
          ) {
            // Log status message
            if (progress.message) {
              addLog('info', progress.message);
            }
            // Don't show progress bar for "0 games" scenarios - nothing to download
            if (progress.message?.includes('0 games')) {
              setPrefillProgress(null);
              return;
            }
            // Set a loading state so UI shows something is happening
            setPrefillProgress({
              ...progress,
              percentComplete: 0,
              bytesDownloaded: 0,
              totalBytes: 0
            });
          } else {
            // Clear progress for any other state (app_completed, etc.)
            setPrefillProgress(null);
          }
        }
      );

      // Handle status changes (daemon status updates)
      connection.on(
        'StatusChanged',
        (_sessionId: string, status: { status: string; message: string }) => {
          if (status.message) {
            addLog('info', `Status: ${status.message}`);
          }
        }
      );

      // Handle prefill state changes
      connection.on('PrefillStateChanged', (_sessionId: string, state: string, durationSeconds?: number) => {
        if (state === 'started') {
          addLog('download', 'Prefill operation started');
          prefillDurationRef.current = 0;
          isReceivingProgressRef.current = true;
          // Clear any previous background completion notification
          clearBackgroundCompletion();
          // Track prefill in progress for background detection
          try {
            sessionStorage.setItem('prefill_in_progress', JSON.stringify({
              startedAt: new Date().toISOString(),
              sessionId: _sessionId
            }));
          } catch { /* ignore */ }
        } else if (state === 'completed') {
          const duration = durationSeconds || prefillDurationRef.current;
          addLog('success', `Prefill completed in ${Math.round(duration)}s`);
          isCancelling.current = false;
          isReceivingProgressRef.current = false;
          setPrefillProgress(null);
          // Clear prefill in progress tracking
          try { sessionStorage.removeItem('prefill_in_progress'); } catch { /* ignore */ }

          // If page was hidden, store background completion for notification
          if (isPageHiddenRef.current) {
            setBackgroundCompletion({
              completedAt: new Date().toISOString(),
              message: `Prefill completed in ${Math.round(duration)}s`,
              duration: duration
            });
          }
        } else if (state === 'failed') {
          addLog('error', 'Prefill operation failed');
          isCancelling.current = false;
          isReceivingProgressRef.current = false;
          setPrefillProgress(null);
          // Clear prefill in progress tracking
          try { sessionStorage.removeItem('prefill_in_progress'); } catch { /* ignore */ }
        } else if (state === 'cancelled') {
          addLog('info', 'Prefill operation cancelled');
          isCancelling.current = false;
          isReceivingProgressRef.current = false;
          setPrefillProgress(null);
          // Clear prefill in progress tracking
          try { sessionStorage.removeItem('prefill_in_progress'); } catch { /* ignore */ }
        }
      });

      // Handle daemon session created (broadcast to all clients)
      connection.on('DaemonSessionCreated', (sessionDto: PrefillSessionDto) => {
        // Update session if it matches our current session
        setSession((currentSession) => {
          if (currentSession && sessionDto.id === currentSession.id) {
            setTimeRemaining(sessionDto.timeRemainingSeconds);
            return sessionDto;
          }
          return currentSession;
        });
      });

      // Handle daemon session updated (broadcast to all clients)
      connection.on('DaemonSessionUpdated', (sessionDto: PrefillSessionDto) => {
        // Update session if it matches our current session
        setSession((currentSession) => {
          if (currentSession && sessionDto.id === currentSession.id) {
            setTimeRemaining(sessionDto.timeRemainingSeconds);
            setIsLoggedIn(sessionDto.authState === 'Authenticated');
            return sessionDto;
          }
          return currentSession;
        });
      });

      // Handle prefill history updated (broadcast to all clients)
      // eslint-disable-next-line @typescript-eslint/no-unused-vars
      connection.on(
        'PrefillHistoryUpdated',
        (_event: { sessionId: string; appId: number; status: string }) => {
          // This event is primarily for admin pages to refresh history views
          // PrefillPanel doesn't need to act on this directly
        }
      );

      connection.onclose((error) => {
        console.log('Hub connection closed:', error);
        setIsConnecting(false);
      });

      connection.onreconnecting((error) => {
        console.log('Hub reconnecting:', error);
        addLog('warning', 'Connection lost, reconnecting...');
      });

      connection.onreconnected(async (connectionId) => {
        console.log('Hub reconnected:', connectionId);
        addLog('success', 'Reconnected to server');
        // Re-subscribe to session if we have one
        if (session) {
          try {
            await connection.invoke('SubscribeToSession', session.id);

            // Check if prefill completed while we were disconnected
            // Query the server for the last prefill result
            try {
              const lastResult = await connection.invoke('GetLastPrefillResult', session.id) as {
                status: string;
                completedAt: string;
                durationSeconds: number;
              } | null;

              if (lastResult && lastResult.status === 'completed') {
                const completedTime = new Date(lastResult.completedAt).getTime();
                const now = Date.now();
                // If completed in last 5 minutes and we don't have a background notification yet
                // and we're not actively receiving progress updates
                if (now - completedTime < 5 * 60 * 1000 && !backgroundCompletion && !isReceivingProgressRef.current) {
                  setBackgroundCompletion({
                    completedAt: lastResult.completedAt,
                    message: `Prefill completed in ${lastResult.durationSeconds}s`,
                    duration: lastResult.durationSeconds
                  });
                  addLog('success', `Prefill completed while disconnected (${lastResult.durationSeconds}s)`);
                }
              }
            } catch (err) {
              console.debug('Failed to check last prefill result:', err);
              // Non-critical - don't fail reconnection
            }

            // Clear any stale tracking flags
            try { sessionStorage.removeItem('prefill_in_progress'); } catch { /* ignore */ }
          } catch (err) {
            console.error('Failed to resubscribe:', err);
          }
        }
      });

      await connection.start();
      hubConnection.current = connection;
      setIsConnecting(false);

      return connection;
    } catch (err) {
      console.error('Failed to connect to hub:', err);
      setError('Failed to connect to server');
      setIsConnecting(false);
      return null;
    }
  }, [session, onSessionEnd, handleAuthStateChanged, addLog, setBackgroundCompletion, clearBackgroundCompletion]);

  // Check for existing sessions and reconnect if found
  const initializeSession = useCallback(async () => {
    if (initializationAttempted.current) return;
    initializationAttempted.current = true;

    setIsInitializing(true);

    try {
      const connection = await connectToHub();
      if (!connection) {
        setIsInitializing(false);
        return;
      }

      // Check for existing sessions
      const existingSessions = await connection.invoke<PrefillSessionDto[]>('GetMySessions');
      const activeSession = existingSessions?.find((s) => s.status === 'Active');

      if (activeSession) {
        addLog('info', 'Reconnecting to existing session...', `Session: ${activeSession.id}`);

        // Subscribe to the session
        await connection.invoke('SubscribeToSession', activeSession.id);

        setSession(activeSession);
        setTimeRemaining(activeSession.timeRemainingSeconds);
        setIsLoggedIn(activeSession.authState === 'Authenticated');

        addLog(
          'success',
          'Reconnected to existing session',
          `Container: ${activeSession.containerName}`
        );
        addLog(
          'info',
          `Session expires in ${formatTimeRemaining(activeSession.timeRemainingSeconds)}`
        );

        if (activeSession.authState === 'Authenticated') {
          addLog('info', 'Already logged in to Steam');
        } else {
          addLog('info', 'Click "Login to Steam" to authenticate');
        }
      }
    } catch (err) {
      console.error('Failed to initialize session:', err);
      // Don't set error - just means no existing session
    } finally {
      setIsInitializing(false);
    }
  }, [connectToHub, addLog, formatTimeRemaining]);

  // Initialize on mount
  useEffect(() => {
    initializeSession();
  }, [initializeSession]);

  const createSession = useCallback(async () => {
    setIsCreating(true);
    setError(null);
    clearLogs(); // Clear previous logs

    try {
      // Connect to hub if not connected
      let connection = hubConnection.current;
      if (!connection || connection.state !== 'Connected') {
        connection = await connectToHub();
      }

      if (!connection) {
        throw new Error('Failed to establish connection');
      }

      addLog('info', 'Creating prefill session...');

      // Create session via hub (returns existing session if one exists)
      const sessionDto = await connection.invoke<PrefillSessionDto>('CreateSession');
      setSession(sessionDto);
      setTimeRemaining(sessionDto.timeRemainingSeconds);

      // Check if this was an existing session or a new one
      const isExistingSession = sessionDto.authState === 'Authenticated';
      setIsLoggedIn(isExistingSession);

      if (isExistingSession) {
        addLog(
          'success',
          'Connected to existing session',
          `Container: ${sessionDto.containerName}`
        );
        addLog('info', 'Already logged in to Steam');
      } else {
        addLog('success', 'Session created successfully', `Container: ${sessionDto.containerName}`);
        addLog('info', 'Click "Login to Steam" to authenticate before using prefill commands');
      }
      addLog('info', `Session expires in ${formatTimeRemaining(sessionDto.timeRemainingSeconds)}`);

      // Subscribe to session to start receiving events
      await connection.invoke('SubscribeToSession', sessionDto.id);

      setIsCreating(false);
    } catch (err) {
      console.error('Failed to create session:', err);
      const errorMessage = err instanceof Error ? err.message : 'Failed to create session';
      setError(errorMessage);
      addLog('error', errorMessage);
      setIsCreating(false);
    }
  }, [connectToHub, formatTimeRemaining, addLog, clearLogs]);

  // Helper to call prefill REST API (bypasses SignalR serialization issues)
  const callPrefillApi = useCallback(
    async (
      sessionId: string,
      options: {
        all?: boolean;
        recent?: boolean;
        recentlyPurchased?: boolean;
        top?: number;
        force?: boolean;
      } = {}
    ) => {
      // Reset cancelling flag when starting a new prefill
      isCancelling.current = false;

      // Build the full request with settings
      const requestBody: Record<string, unknown> = { ...options };

      // Add OS selection (only if not all platforms selected)
      if (selectedOS.length > 0 && selectedOS.length < 3) {
        requestBody.operatingSystems = selectedOS;
      }

      // Add max concurrency if not default
      if (maxConcurrency !== 'default') {
        requestBody.maxConcurrency = parseInt(maxConcurrency, 10);
      }

      const response = await fetch(`${API_BASE}/prefill-daemon/sessions/${sessionId}/prefill`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...authService.getAuthHeaders()
        },
        body: JSON.stringify(requestBody)
      });

      if (!response.ok) {
        const error = await response.json().catch(() => ({ message: 'Prefill request failed' }));
        throw new Error(error.message || `HTTP ${response.status}`);
      }

      return response.json();
    },
    [selectedOS, maxConcurrency]
  );

  const executeCommand = useCallback(
    async (commandType: CommandType) => {
      if (!session || !hubConnection.current) return;

      setIsExecuting(true);
      addLog('command', `Running: ${commandType}`);

      try {
        switch (commandType) {
          case 'select-apps': {
            // Get owned games list via REST API
            setIsLoadingGames(true);
            try {
              const response = await fetch(
                `${API_BASE}/prefill-daemon/sessions/${session.id}/games`,
                {
                  headers: authService.getAuthHeaders()
                }
              );
              if (!response.ok) {
                throw new Error(`Failed to get games: HTTP ${response.status}`);
              }
              const games = await response.json();
              setOwnedGames(games || []);
              addLog('info', `Found ${games?.length || 0} owned games`);
              setShowGameSelection(true);
            } finally {
              setIsLoadingGames(false);
            }
            break;
          }
          case 'prefill': {
            // Check if any games are selected
            if (selectedAppIds.length === 0) {
              addLog(
                'warning',
                'No games selected. Use "Select Apps" to choose games for prefill first.'
              );
              break;
            }
            addLog('download', `Starting prefill of ${selectedAppIds.length} selected apps...`);
            const result = await callPrefillApi(session.id, {});
            setPrefillProgress(null); // Clear progress on completion
            if (result?.success) {
              const totalSeconds = result.totalSeconds || 0;
              addLog('success', `Prefill completed in ${Math.round(totalSeconds)}s`);
            } else {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'prefill-all': {
            addLog('download', 'Starting prefill of all owned games...');
            const result = await callPrefillApi(session.id, { all: true });
            setPrefillProgress(null); // Clear progress on completion
            if (result?.success) {
              const totalSeconds = result.totalSeconds || 0;
              addLog('success', `Prefill completed in ${Math.round(totalSeconds)}s`);
            } else {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'prefill-recent': {
            addLog('download', 'Starting prefill of recently played games...');
            const result = await callPrefillApi(session.id, { recent: true });
            setPrefillProgress(null); // Clear progress on completion
            if (result?.success) {
              const totalSeconds = result.totalSeconds || 0;
              addLog('success', `Prefill completed in ${Math.round(totalSeconds)}s`);
            } else {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'prefill-recent-purchased': {
            addLog('download', 'Starting prefill of recently purchased games...');
            const result = await callPrefillApi(session.id, { recentlyPurchased: true });
            setPrefillProgress(null); // Clear progress on completion
            if (result?.success) {
              const totalSeconds = result.totalSeconds || 0;
              addLog('success', `Prefill completed in ${Math.round(totalSeconds)}s`);
            } else {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'prefill-top': {
            addLog('download', 'Starting prefill of top 50 popular games...');
            const result = await callPrefillApi(session.id, { top: 50 });
            setPrefillProgress(null); // Clear progress on completion
            if (result?.success) {
              const totalSeconds = result.totalSeconds || 0;
              addLog('success', `Prefill completed in ${Math.round(totalSeconds)}s`);
            } else {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'prefill-force': {
            addLog('download', 'Starting force prefill (re-downloading)...');
            const result = await callPrefillApi(session.id, { force: true });
            setPrefillProgress(null); // Clear progress on completion
            if (result?.success) {
              const totalSeconds = result.totalSeconds || 0;
              addLog('success', `Prefill completed in ${Math.round(totalSeconds)}s`);
            } else {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'clear-temp': {
            addLog('info', 'Clearing temporary cache...');
            try {
              const clearResult = await hubConnection.current.invoke('ClearCache', session.id);
              if (clearResult?.success) {
                addLog('success', clearResult.message || 'Cache cleared successfully');
              } else {
                addLog('error', clearResult?.message || 'Failed to clear cache');
              }
            } catch {
              addLog('warning', 'Clear cache not supported by current daemon version');
            }
            break;
          }
          default:
            addLog('warning', `Command '${commandType}' not yet implemented`);
        }
      } catch (err) {
        console.error('Failed to execute command:', err);
        const errorMessage = err instanceof Error ? err.message : 'Failed to execute command';
        addLog('error', errorMessage);
      } finally {
        setTimeout(() => setIsExecuting(false), 1000);
      }
    },
    [session, addLog, selectedAppIds, ownedGames, callPrefillApi]
  );

  const handleEndSession = useCallback(async () => {
    if (!session || !hubConnection.current) return;

    try {
      await hubConnection.current.invoke('EndSession', session.id);
    } catch (err) {
      console.error('Failed to end session:', err);
    }

    setSession(null);
    setIsExecuting(false);
    setIsLoggedIn(false);
    onSessionEnd?.();
  }, [session, onSessionEnd]);

  const handleCancelLogin = useCallback(async () => {
    if (!session || !hubConnection.current) return;

    try {
      await hubConnection.current.invoke('CancelLogin', session.id);
      addLog('info', 'Login cancelled');
    } catch (err) {
      console.error('Failed to cancel login:', err);
    }
  }, [session, addLog]);

  const handleCancelPrefill = useCallback(async () => {
    if (!session || !hubConnection.current) return;

    // Set cancelling flag to prevent incoming progress events from re-setting state
    isCancelling.current = true;

    try {
      await hubConnection.current.invoke('CancelPrefill', session.id);
      addLog('info', 'Prefill cancellation requested');
      setPrefillProgress(null);
    } catch (err) {
      console.error('Failed to cancel prefill:', err);
      addLog('error', 'Failed to cancel prefill');
      // Reset flag on error so user can try again
      isCancelling.current = false;
    }
  }, [session, addLog]);

  const handleOpenAuthModal = useCallback(() => {
    authActions.resetAuthForm();
    setShowAuthModal(true);
  }, [authActions]);

  // Handle saving game selection via REST API
  const handleSaveGameSelection = useCallback(
    async (selectedIds: number[]) => {
      if (!session) return;

      const response = await fetch(
        `${API_BASE}/prefill-daemon/sessions/${session.id}/selected-apps`,
        {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            ...authService.getAuthHeaders()
          },
          body: JSON.stringify({ appIds: selectedIds })
        }
      );

      if (!response.ok) {
        throw new Error(`Failed to set selected apps: HTTP ${response.status}`);
      }

      setSelectedAppIds(selectedIds);
      addLog('success', `Selected ${selectedIds.length} games for prefill`);
    },
    [session, addLog]
  );

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      hubConnection.current?.stop();
    };
  }, []);

  // Track page visibility for background completion detection
  useEffect(() => {
    const handleVisibilityChange = async () => {
      const wasHidden = isPageHiddenRef.current;
      isPageHiddenRef.current = document.hidden;

      // When page becomes visible, check if prefill completed while away
      if (wasHidden && !document.hidden && session && hubConnection.current?.state === 'Connected') {
        // Query the server for the last prefill result
        // This reliably detects completion even if WebSocket was disconnected
        try {
          const lastResult = await hubConnection.current.invoke('GetLastPrefillResult', session.id) as {
            status: string;
            completedAt: string;
            durationSeconds: number;
          } | null;

          if (lastResult && lastResult.status === 'completed') {
            const completedTime = new Date(lastResult.completedAt).getTime();
            const now = Date.now();
            // If completed in last 5 minutes and we don't have a background notification yet
            // and we're not actively receiving progress updates
            if (now - completedTime < 5 * 60 * 1000 && !backgroundCompletion && !isReceivingProgressRef.current) {
              setBackgroundCompletion({
                completedAt: lastResult.completedAt,
                message: `Prefill completed in ${lastResult.durationSeconds}s`,
                duration: lastResult.durationSeconds
              });
              addLog('success', `Prefill completed while in background (${lastResult.durationSeconds}s)`);
            }
          }
        } catch (err) {
          console.debug('Failed to check last prefill result on visibility change:', err);
          // Non-critical - don't show error to user
        }

        // Clear any stale tracking flags
        try { sessionStorage.removeItem('prefill_in_progress'); } catch { /* ignore */ }
      }
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, [session, backgroundCompletion, setBackgroundCompletion, addLog]);

  // Auto-dismiss background completion notification after 10 seconds
  useEffect(() => {
    if (backgroundCompletion) {
      const timer = setTimeout(() => {
        clearBackgroundCompletion();
      }, 10000);
      return () => clearTimeout(timer);
    }
  }, [backgroundCompletion, clearBackgroundCompletion]);

  // Commands that need confirmation before execution
  const COMMANDS_REQUIRING_CONFIRMATION: CommandType[] = ['prefill', 'prefill-all', 'prefill-top'];

  const getConfirmationMessage = (commandType: CommandType): { title: string; message: string } => {
    switch (commandType) {
      case 'prefill':
        return {
          title: 'Prefill Selected Games?',
          message: `This will download ${selectedAppIds.length} selected game${selectedAppIds.length !== 1 ? 's' : ''} to your cache.`
        };
      case 'prefill-all':
        return {
          title: 'Prefill All Games?',
          message:
            'This will download ALL games in your Steam library. Depending on your library size, this could be hundreds of gigabytes and take many hours. Are you sure you want to continue?'
        };
      case 'prefill-top':
        return {
          title: 'Prefill Top 50 Games?',
          message:
            'This will download the 50 most popular games. This could be several hundred gigabytes of data. Are you sure you want to continue?'
        };
      default:
        return { title: 'Confirm', message: 'Are you sure?' };
    }
  };

  // Fetch estimated download size for selected apps
  const fetchEstimatedSize = useCallback(async () => {
    if (!session?.id || !hubConnection.current) return;

    setEstimatedSize({ bytes: 0, loading: true });

    try {
      // Pass selected operating systems to get accurate size calculations
      const status = (await hubConnection.current.invoke('GetSelectedAppsStatus', session.id, selectedOS)) as {
        totalDownloadSize: number;
        message?: string;
        apps?: Array<{
          appId: number;
          name: string;
          downloadSize: number;
          isUnsupportedOs?: boolean;
          unavailableReason?: string;
        }>;
      };
      setEstimatedSize({
        bytes: status.totalDownloadSize,
        loading: false,
        apps: status.apps,
        message: status.message
      });
    } catch (error) {
      console.error('Failed to fetch estimated size:', error);
      setEstimatedSize({ bytes: 0, loading: false, error: 'Could not estimate size' });
    }
  }, [session?.id, selectedOS]);

  // Handle button click - show confirmation for large operations
  const handleCommandClick = useCallback(
    async (commandType: CommandType) => {
      if (COMMANDS_REQUIRING_CONFIRMATION.includes(commandType)) {
        setPendingConfirmCommand(commandType);
        // Fetch estimated size for prefill selected (apps are already set)
        if (commandType === 'prefill' && selectedAppIds.length > 0) {
          await fetchEstimatedSize();
        } else {
          // For prefill-all and prefill-top, we can't easily estimate without selecting first
          setEstimatedSize({ bytes: 0, loading: false });
        }
      } else {
        executeCommand(commandType);
      }
    },
    [executeCommand, selectedAppIds.length, fetchEstimatedSize]
  );

  // Handle confirmation
  const handleConfirmCommand = useCallback(() => {
    if (pendingConfirmCommand) {
      executeCommand(pendingConfirmCommand);
      setPendingConfirmCommand(null);
      setEstimatedSize({ bytes: 0, loading: false });
    }
  }, [pendingConfirmCommand, executeCommand]);

  const handleCancelConfirm = useCallback(() => {
    setPendingConfirmCommand(null);
    setEstimatedSize({ bytes: 0, loading: false });
  }, []);

  // Render command button - ALL commands disabled until logged in
  const renderCommandButton = (cmd: CommandButton) => {
    // Special handling for "Prefill Selected" - disable if no games selected
    const isPrefillSelected = cmd.id === 'prefill';
    const noGamesSelected = selectedAppIds.length === 0;
    const isDisabled = isExecuting || !isLoggedIn || (isPrefillSelected && noGamesSelected);

    // Dynamic label for prefill selected
    const label =
      isPrefillSelected && selectedAppIds.length > 0
        ? `Prefill Selected (${selectedAppIds.length})`
        : cmd.label;

    // Dynamic description for prefill selected
    const description = isPrefillSelected
      ? noGamesSelected
        ? 'Select games first'
        : `${selectedAppIds.length} game${selectedAppIds.length !== 1 ? 's' : ''} ready`
      : cmd.description;

    return (
      <Button
        key={cmd.id}
        variant={cmd.variant || 'default'}
        color={cmd.color}
        onClick={() => handleCommandClick(cmd.id)}
        disabled={isDisabled}
        className="h-auto py-3 px-4 flex-col items-start gap-1"
        size="sm"
      >
        <div className="flex items-center gap-2 w-full">
          <span
            className="p-1.5 rounded-md"
            style={{
              backgroundColor:
                cmd.variant === 'filled'
                  ? 'rgba(255,255,255,0.15)'
                  : 'color-mix(in srgb, var(--theme-primary) 15%, transparent)'
            }}
          >
            {cmd.icon}
          </span>
          <span className="font-medium text-sm">{label}</span>
        </div>
        <span className="text-xs opacity-70 pl-8">{description}</span>
      </Button>
    );
  };

  // No session state - show start screen
  if (!session && !isCreating && !isInitializing) {
    return (
      <div className="animate-fade-in">
        {/* Steam Auth Modal */}
        <SteamAuthModal
          opened={showAuthModal}
          onClose={() => setShowAuthModal(false)}
          state={authState}
          actions={authActions}
          isPrefillMode={true}
          onCancelLogin={handleCancelLogin}
        />

        <Card className="max-w-2xl mx-auto">
          <CardContent className="py-12">
            <div className="flex flex-col items-center text-center space-y-6">
              {/* Steam Icon */}
              <div className="w-20 h-20 rounded-2xl flex items-center justify-center bg-[var(--theme-steam)]">
                <SteamIcon size={40} className="text-white" />
              </div>

              <div className="space-y-2">
                <h2 className="text-2xl font-bold text-themed-primary">Steam Prefill</h2>
                <p className="text-themed-muted max-w-md">
                  Pre-download Steam games to your cache for faster LAN party downloads. Connect to
                  your Steam library and prefill games before the event.
                </p>
              </div>

              {error && (
                <div
                  className="w-full max-w-md p-4 rounded-lg flex items-center gap-3 bg-[var(--theme-error-bg)] border border-[color-mix(in_srgb,var(--theme-error)_30%,transparent)]"
                >
                  <AlertCircle className="h-5 w-5 flex-shrink-0 text-[var(--theme-error)]" />
                  <span className="text-sm text-[var(--theme-error-text)]">
                    {error}
                  </span>
                </div>
              )}

              <div className="flex flex-col items-center gap-3 pt-2">
                <Button
                  onClick={createSession}
                  disabled={isConnecting}
                  variant="filled"
                  size="lg"
                  className="min-w-[200px]"
                >
                  {isConnecting ? (
                    <>
                      <Loader2 className="h-5 w-5 animate-spin" />
                      Connecting...
                    </>
                  ) : (
                    <>
                      <Play className="h-5 w-5" />
                      Start Session
                    </>
                  )}
                </Button>
                <p className="text-xs text-themed-muted flex items-center gap-1.5">
                  <Shield className="h-3.5 w-3.5" />
                  Requires Steam login to access your game library
                </p>
              </div>
            </div>
          </CardContent>
        </Card>
      </div>
    );
  }

  // Loading/Creating state
  if (isInitializing || isCreating) {
    return (
      <div className="animate-fade-in">
        <Card className="max-w-2xl mx-auto">
          <CardContent className="py-12">
            <div className="flex flex-col items-center justify-center gap-4">
              <div
                className="w-16 h-16 rounded-xl flex items-center justify-center bg-[color-mix(in_srgb,var(--theme-steam)_15%,transparent)]"
              >
                <Loader2 className="h-8 w-8 animate-spin text-[var(--theme-steam)]" />
              </div>
              <div className="text-center">
                <p className="text-lg font-medium text-themed-primary">
                  {isInitializing ? 'Checking for existing session...' : 'Creating session...'}
                </p>
                <p className="text-sm text-themed-muted mt-1">This may take a moment</p>
              </div>
            </div>
          </CardContent>
        </Card>
      </div>
    );
  }

  // Active session - full interface
  return (
    <div className="space-y-4 animate-fade-in">
      {/* Steam Auth Modal */}
      <SteamAuthModal
        opened={showAuthModal}
        onClose={() => setShowAuthModal(false)}
        state={authState}
        actions={authActions}
        isPrefillMode={true}
        onCancelLogin={handleCancelLogin}
      />

      {/* Game Selection Modal */}
      <GameSelectionModal
        opened={showGameSelection}
        onClose={() => setShowGameSelection(false)}
        games={ownedGames}
        selectedAppIds={selectedAppIds}
        onSave={handleSaveGameSelection}
        isLoading={isLoadingGames}
      />

      {/* Large Prefill Confirmation Dialog */}
      <Modal
        opened={!!pendingConfirmCommand}
        onClose={handleCancelConfirm}
        title={
          <div className="flex items-center gap-3">
            <div
              className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0 bg-[color-mix(in_srgb,var(--theme-warning)_15%,transparent)]"
            >
              <AlertCircle className="h-5 w-5 text-[var(--theme-warning)]" />
            </div>
            <span>
              {pendingConfirmCommand ? getConfirmationMessage(pendingConfirmCommand).title : ''}
            </span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-sm text-themed-muted">
            {pendingConfirmCommand ? getConfirmationMessage(pendingConfirmCommand).message : ''}
          </p>

          {/* Estimated download size */}
          {pendingConfirmCommand === 'prefill' && (
            <div className="p-3 rounded-lg bg-[var(--theme-bg-secondary)]">
              {estimatedSize.loading ? (
                <div className="flex items-center gap-2">
                  <Loader2 className="h-4 w-4 animate-spin text-[var(--theme-primary)]" />
                  <span className="text-sm text-themed-muted">Calculating download size...</span>
                </div>
              ) : estimatedSize.error ? (
                <span className="text-sm text-themed-muted">{estimatedSize.error}</span>
              ) : (
                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="text-sm text-themed-muted">Total estimated download:</span>
                    <span className="text-sm font-semibold text-[var(--theme-primary)]">
                      {formatBytes(estimatedSize.bytes)}
                    </span>
                  </div>
                  {estimatedSize.apps && estimatedSize.apps.length > 0 && (
                    <div className="pt-2 border-t border-[var(--theme-border-primary)]">
                      <div className="text-xs text-themed-muted mb-1">
                        Breakdown ({estimatedSize.apps.length} games):
                      </div>
                      <div className="space-y-1 max-h-32 overflow-y-auto">
                        {estimatedSize.apps.map((app) => (
                          <div
                            key={app.appId}
                            className={`flex items-center justify-between text-xs ${
                              app.isUnsupportedOs ? 'opacity-50' : ''
                            }`}
                          >
                            <span
                              className={`truncate mr-2 max-w-[200px] ${
                                app.isUnsupportedOs
                                  ? 'text-themed-muted line-through'
                                  : 'text-themed-secondary'
                              }`}
                              title={app.unavailableReason || app.name}
                            >
                              {app.name}
                            </span>
                            <span
                              className={`whitespace-nowrap ${
                                app.isUnsupportedOs ? 'text-amber-500' : 'text-themed-muted'
                              }`}
                              title={app.unavailableReason}
                            >
                              {app.isUnsupportedOs
                                ? app.unavailableReason || 'Unsupported OS'
                                : formatBytes(app.downloadSize)}
                            </span>
                          </div>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}

          <div className="flex justify-end gap-3 pt-2">
            <Button variant="outline" onClick={handleCancelConfirm}>
              Cancel
            </Button>
            <Button
              variant="filled"
              color="blue"
              onClick={handleConfirmCommand}
              disabled={pendingConfirmCommand === 'prefill' && estimatedSize.loading}
            >
              {pendingConfirmCommand === 'prefill' ? 'Start Download' : 'Yes, Continue'}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Header Bar */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4 p-4 rounded-lg bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)]">
        <div className="flex items-center gap-4">
          <div className="w-12 h-12 rounded-xl flex items-center justify-center flex-shrink-0 bg-[var(--theme-steam)]">
            <SteamIcon size={24} className="text-white" />
          </div>
          <div>
            <h1 className="text-xl font-bold text-themed-primary">Steam Prefill</h1>
            <p className="text-sm text-themed-muted">Pre-download games to your cache</p>
          </div>
        </div>

        <div className="flex items-center gap-3 w-full sm:w-auto">
          {/* Session Timer */}
          <div
            className={`flex items-center gap-2 px-4 py-2 rounded-lg flex-1 sm:flex-initial justify-center border ${
              timeRemaining < 600
                ? 'bg-[color-mix(in_srgb,var(--theme-warning)_15%,transparent)] border-[color-mix(in_srgb,var(--theme-warning)_30%,transparent)]'
                : 'bg-[var(--theme-bg-tertiary)] border-[var(--theme-border-secondary)]'
            }`}
          >
            <Timer
              className={`h-4 w-4 ${
                timeRemaining < 600 ? 'text-[var(--theme-warning)]' : 'text-[var(--theme-text-muted)]'
              }`}
            />
            <span
              className={`font-mono font-semibold tabular-nums ${
                timeRemaining < 600 ? 'text-[var(--theme-warning-text)]' : 'text-[var(--theme-text-primary)]'
              }`}
            >
              {formatTimeRemaining(timeRemaining)}
            </span>
          </div>

          {/* End Session Button */}
          <Button
            variant="outline"
            size="sm"
            onClick={handleEndSession}
            className="flex-shrink-0 border-[color-mix(in_srgb,var(--theme-error)_40%,transparent)] text-[var(--theme-error)]"
          >
            <X className="h-4 w-4" />
            <span className="hidden sm:inline">End Session</span>
          </Button>
        </div>
      </div>

      {/* Error Banner */}
      {error && (
        <div
          className="p-4 rounded-lg flex items-center gap-3 bg-[var(--theme-error-bg)] border border-[color-mix(in_srgb,var(--theme-error)_30%,transparent)]"
        >
          <AlertCircle className="h-5 w-5 flex-shrink-0 text-[var(--theme-error)]" />
          <span className="text-[var(--theme-error-text)]">{error}</span>
        </div>
      )}

      {/* Main Content - Two Column Layout */}
      <div className="grid grid-cols-1 xl:grid-cols-3 gap-4">
        {/* Left Column - Controls */}
        <div className="xl:col-span-2 space-y-4">
          {/* Authentication Card */}
          <Card padding="md">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-3">
                <div
                  className={`w-10 h-10 rounded-lg flex items-center justify-center ${
                    isLoggedIn
                      ? 'bg-[color-mix(in_srgb,var(--theme-success)_15%,transparent)]'
                      : 'bg-[color-mix(in_srgb,var(--theme-warning)_15%,transparent)]'
                  }`}
                >
                  {isLoggedIn ? (
                    <CheckCircle2 className="h-5 w-5 text-[var(--theme-success)]" />
                  ) : (
                    <LogIn className="h-5 w-5 text-[var(--theme-warning)]" />
                  )}
                </div>
                <div>
                  <p className="font-medium text-themed-primary">
                    {isLoggedIn ? 'Logged In to Steam' : 'Steam Login Required'}
                  </p>
                  <p className="text-sm text-themed-muted">
                    {isLoggedIn
                      ? 'You can now use prefill commands'
                      : 'Authenticate to access your game library'}
                  </p>
                </div>
              </div>

              {!isLoggedIn && (
                <Button variant="filled" onClick={handleOpenAuthModal} className="flex-shrink-0">
                  <SteamIcon size={18} />
                  Login to Steam
                </Button>
              )}
            </div>
          </Card>

          {/* Network Status Card */}
          <NetworkStatusSection diagnostics={session?.networkDiagnostics} />

          {/* Background Completion Notification Banner */}
          {backgroundCompletion && !prefillProgress && (
            <Card
              padding="md"
              className="overflow-hidden border-[color-mix(in_srgb,var(--theme-success)_50%,transparent)] animate-fade-in"
            >
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-3">
                  <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[color-mix(in_srgb,var(--theme-success)_15%,transparent)]">
                    <CheckCircle2 className="h-5 w-5 text-[var(--theme-success)]" />
                  </div>
                  <div>
                    <p className="font-medium text-themed-primary">Download Completed</p>
                    <p className="text-sm text-themed-muted">
                      {backgroundCompletion.message}
                    </p>
                  </div>
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={clearBackgroundCompletion}
                  className="flex-shrink-0"
                >
                  <X className="h-4 w-4" />
                  Dismiss
                </Button>
              </div>
            </Card>
          )}

          {/* Download Progress Card */}
          {prefillProgress && (
            <Card
              padding="md"
              className="overflow-hidden border-[color-mix(in_srgb,var(--theme-primary)_50%,transparent)]"
            >
              <div className="space-y-4">
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-3">
                    <div
                      className="w-10 h-10 rounded-lg flex items-center justify-center bg-[color-mix(in_srgb,var(--theme-primary)_15%,transparent)]"
                    >
                      <Download
                        className="h-5 w-5 animate-pulse text-[var(--theme-primary)]"
                      />
                    </div>
                    <div>
                      <p className="font-medium text-themed-primary">
                        {prefillProgress.state === 'loading-metadata'
                          ? 'Loading Game Data'
                          : prefillProgress.state === 'metadata-loaded'
                            ? 'Preparing Download'
                            : prefillProgress.state === 'starting'
                              ? 'Starting'
                              : prefillProgress.state === 'preparing'
                                ? 'Preparing'
                                : 'Downloading'}
                      </p>
                      {prefillProgress.state === 'downloading' && (
                        <p className="text-sm text-themed-muted truncate max-w-[300px]">
                          {prefillProgress.currentAppName || `App ${prefillProgress.currentAppId}`}
                        </p>
                      )}
                    </div>
                  </div>

                  <div className="flex items-center gap-3">
                    {prefillProgress.state === 'downloading' && (
                      <div className="text-right hidden sm:block">
                        <p className="text-sm font-medium text-themed-primary">
                          {formatSpeed(prefillProgress.bytesPerSecond)}
                        </p>
                        <p className="text-xs text-themed-muted">
                          {formatTimeRemaining(Math.floor(prefillProgress.elapsedSeconds))} elapsed
                        </p>
                      </div>
                    )}
                    <Button variant="outline" size="sm" onClick={handleCancelPrefill}>
                      <XCircle className="h-4 w-4" />
                      Cancel
                    </Button>
                  </div>
                </div>

                {/* Progress Bar */}
                <div className="space-y-2">
                  <div className="h-3 rounded-full overflow-hidden bg-[var(--theme-progress-bg)]">
                    {prefillProgress.state === 'downloading' ? (
                      <div
                        className="h-full rounded-full transition-all duration-300 ease-out bg-gradient-to-r from-[var(--theme-primary)] to-[var(--theme-accent)]"
                        style={{ width: `${Math.min(100, prefillProgress.percentComplete)}%` }}
                      />
                    ) : (
                      <div
                        className="h-full rounded-full animate-pulse w-full opacity-50 bg-gradient-to-r from-[var(--theme-primary)] to-[var(--theme-accent)]"
                      />
                    )}
                  </div>

                  {prefillProgress.state === 'downloading' ? (
                    <div className="flex items-center justify-between text-xs text-themed-muted">
                      <span>
                        {formatBytes(prefillProgress.bytesDownloaded)} /{' '}
                        {formatBytes(prefillProgress.totalBytes)}
                      </span>
                      <span className="font-medium text-[var(--theme-primary)]">
                        {prefillProgress.percentComplete.toFixed(1)}%
                      </span>
                    </div>
                  ) : (
                    <p className="text-sm text-themed-muted text-center">
                      {prefillProgress.message || 'Preparing prefill operation...'}
                    </p>
                  )}
                </div>
              </div>
            </Card>
          )}

          {/* Command Buttons */}
          <Card padding="md">
            <div className="space-y-6">
              {/* Selection Commands */}
              <div>
                <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted mb-3 flex items-center gap-2">
                  <List className="h-3.5 w-3.5" />
                  Game Selection
                </h3>
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
                  {SELECTION_COMMANDS.map(renderCommandButton)}
                </div>
              </div>

              {/* Download Settings */}
              <div>
                <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted mb-3 flex items-center gap-2">
                  <Settings className="h-3.5 w-3.5" />
                  Download Settings
                </h3>
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                  {/* OS Selection */}
                  <div>
                    <label className="text-sm font-medium text-themed-secondary mb-1.5 flex items-center gap-2">
                      <Monitor className="h-3.5 w-3.5" />
                      Target Platforms
                    </label>
                    <MultiSelectDropdown
                      options={OS_OPTIONS}
                      values={selectedOS}
                      onChange={setSelectedOS}
                      disabled={isExecuting || !isLoggedIn}
                      minSelections={1}
                      placeholder="Select platforms"
                    />
                  </div>
                  {/* Thread/Concurrency Selection */}
                  <div>
                    <label className="text-sm font-medium text-themed-secondary mb-1.5 flex items-center gap-2">
                      <Cpu className="h-3.5 w-3.5" />
                      Download Threads
                    </label>
                    <EnhancedDropdown
                      options={THREAD_OPTIONS}
                      value={maxConcurrency}
                      onChange={setMaxConcurrency}
                      disabled={isExecuting || !isLoggedIn}
                    />
                  </div>
                </div>
              </div>

              {/* Prefill Commands */}
              <div>
                <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted mb-3 flex items-center gap-2">
                  <Download className="h-3.5 w-3.5" />
                  Prefill Options
                </h3>
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
                  {PREFILL_COMMANDS.map(renderCommandButton)}
                </div>
              </div>

              {/* Utility Commands */}
              <div>
                <h3 className="text-xs font-semibold uppercase tracking-wider text-themed-muted mb-3 flex items-center gap-2">
                  <Zap className="h-3.5 w-3.5" />
                  Utilities
                </h3>
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
                  {UTILITY_COMMANDS.map(renderCommandButton)}
                </div>
              </div>

              {/* Login Required Notice */}
              {!isLoggedIn && (
                <div
                  className="p-4 rounded-lg flex items-start gap-3 bg-[color-mix(in_srgb,var(--theme-warning)_10%,transparent)] border border-[color-mix(in_srgb,var(--theme-warning)_25%,transparent)]"
                >
                  <Shield
                    className="h-5 w-5 flex-shrink-0 mt-0.5 text-[var(--theme-warning)]"
                  />
                  <div>
                    <p
                      className="font-medium text-sm text-[var(--theme-warning-text)]"
                    >
                      Login Required to Use Commands
                    </p>
                    <p className="text-sm text-themed-muted mt-1">
                      All prefill commands require Steam authentication. Click "Login to Steam"
                      above to enable commands. Your credentials are sent directly to the container
                      and never stored by this application.
                    </p>
                  </div>
                </div>
              )}
            </div>
          </Card>
        </div>

        {/* Right Column - Activity Log */}
        <div className="xl:col-span-1">
          <Card padding="none" className="overflow-hidden">
            <div className="px-4 pt-4 pb-3 flex items-center gap-3 border-b border-[var(--theme-border-primary)]">
              <div
                className="w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 bg-[color-mix(in_srgb,var(--theme-accent)_15%,transparent)]"
              >
                <ScrollText className="h-4 w-4 text-[var(--theme-accent)]" />
              </div>
              <div>
                <h3 className="text-base font-semibold text-themed-primary">Activity Log</h3>
                <p className="text-xs text-themed-muted">Status updates and output</p>
              </div>
            </div>
            <CardContent className="p-0">
              <ActivityLog entries={logEntries} className="border-0 rounded-none" />
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
