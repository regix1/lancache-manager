import { useEffect, useRef, useCallback, useState } from 'react';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Card, CardContent, CardHeader, CardTitle } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { usePrefillSteamAuth } from '@hooks/usePrefillSteamAuth';
import { ActivityLog, createLogEntry, type LogEntry, type LogEntryType } from './ActivityLog';
import { GameSelectionModal, type OwnedGame } from './GameSelectionModal';
import authService from '@services/auth.service';
import { SIGNALR_BASE } from '@utils/constants';
import {
  Loader2,
  ScrollText,
  X,
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
  CheckCircle2
} from 'lucide-react';

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
}

interface PrefillPanelProps {
  onSessionEnd?: () => void;
}

type CommandType =
  | 'select-apps'
  | 'select-status'
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
}

const COMMAND_BUTTONS: CommandButton[] = [
  {
    id: 'select-apps',
    label: 'Select Apps',
    description: 'Choose games to prefill',
    icon: <List className="h-4 w-4" />,
    variant: 'filled',
    requiresLogin: true
  },
  {
    id: 'select-status',
    label: 'View Selected',
    description: 'Show selected apps',
    icon: <Gamepad2 className="h-4 w-4" />,
    variant: 'outline'
  },
  {
    id: 'prefill',
    label: 'Prefill Selected',
    description: 'Download to cache',
    icon: <Download className="h-4 w-4" />,
    variant: 'filled',
    requiresLogin: true
  },
  {
    id: 'prefill-all',
    label: 'Prefill All',
    description: 'All owned games',
    icon: <Download className="h-4 w-4" />,
    variant: 'subtle',
    requiresLogin: true
  },
  {
    id: 'prefill-recent',
    label: 'Recent Played',
    description: 'Last 2 weeks',
    icon: <Clock className="h-4 w-4" />,
    variant: 'subtle',
    requiresLogin: true
  },
  {
    id: 'prefill-recent-purchased',
    label: 'Recent Bought',
    description: 'Last 2 weeks',
    icon: <ShoppingCart className="h-4 w-4" />,
    variant: 'subtle',
    requiresLogin: true
  },
  {
    id: 'prefill-top',
    label: 'Top 50',
    description: 'Popular games',
    icon: <TrendingUp className="h-4 w-4" />,
    variant: 'subtle',
    requiresLogin: true
  },
  {
    id: 'prefill-force',
    label: 'Force Download',
    description: 'Re-download all',
    icon: <RefreshCw className="h-4 w-4" />,
    variant: 'outline',
    requiresLogin: true
  },
  {
    id: 'clear-temp',
    label: 'Clear Temp',
    description: 'Free disk space',
    icon: <Trash2 className="h-4 w-4" />,
    variant: 'outline'
  }
];

export function PrefillPanel({ onSessionEnd }: PrefillPanelProps) {
  const hubConnection = useRef<HubConnection | null>(null);

  const [session, setSession] = useState<PrefillSessionDto | null>(null);
  const [isConnecting, setIsConnecting] = useState(false);
  const [isCreating, setIsCreating] = useState(false);
  const [isExecuting, setIsExecuting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [timeRemaining, setTimeRemaining] = useState<number>(0);
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [isLoggedIn, setIsLoggedIn] = useState(false);
  const [logEntries, setLogEntries] = useState<LogEntry[]>([]);

  // Game selection state
  const [ownedGames, setOwnedGames] = useState<OwnedGame[]>([]);
  const [selectedAppIds, setSelectedAppIds] = useState<number[]>([]);
  const [showGameSelection, setShowGameSelection] = useState(false);
  const [isLoadingGames, setIsLoadingGames] = useState(false);

  // Prefill progress state
  const [prefillProgress, setPrefillProgress] = useState<{
    state: string;
    currentAppId: number;
    currentAppName?: string;
    percentComplete: number;
    bytesDownloaded: number;
    totalBytes: number;
    bytesPerSecond: number;
    elapsedSeconds: number;
  } | null>(null);

  // Helper to add log entries
  const addLog = useCallback((type: LogEntryType, message: string, details?: string) => {
    setLogEntries(prev => [...prev, createLogEntry(type, message, details)]);
  }, []);

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
    }
  });

  /**
   * Handle auth state changes from backend SignalR events
   */
  const handleAuthStateChanged = useCallback((newState: SteamAuthState) => {
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
  }, [authActions, trigger2FAPrompt, triggerEmailPrompt, addLog]);

  // Timer for session countdown
  useEffect(() => {
    if (!session || session.status !== 'Active') return;

    const interval = setInterval(() => {
      const remaining = Math.max(0, Math.floor((new Date(session.expiresAt).getTime() - Date.now()) / 1000));
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
        } else if (trimmed.includes('Success') || trimmed.includes('Complete') || trimmed.includes('Done')) {
          type = 'success';
        } else if (trimmed.includes('Warning') || trimmed.includes('warn')) {
          type = 'warning';
        } else if (trimmed.includes('Download') || trimmed.includes('Prefill') || trimmed.includes('%')) {
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
      connection.on('PrefillProgress', (_sessionId: string, progress: {
        state: string;
        currentAppId: number;
        currentAppName?: string;
        percentComplete: number;
        bytesDownloaded: number;
        totalBytes: number;
        bytesPerSecond: number;
        elapsedSeconds: number;
      }) => {
        if (progress.state === 'downloading') {
          setPrefillProgress(progress);
        } else if (progress.state === 'completed' || progress.state === 'error' || progress.state === 'app_completed') {
          // Clear progress on completion or error
          if (progress.state !== 'app_completed') {
            setPrefillProgress(null);
          }
        }
      });

      // Handle status changes (daemon status updates)
      connection.on('StatusChanged', (_sessionId: string, status: { status: string; message: string }) => {
        if (status.message) {
          addLog('info', `Status: ${status.message}`);
        }
      });

      // Handle prefill state changes
      connection.on('PrefillStateChanged', (_sessionId: string, state: string) => {
        if (state === 'started') {
          addLog('download', 'Prefill operation started');
        } else if (state === 'completed') {
          addLog('success', 'Prefill operation completed');
          setPrefillProgress(null);
        } else if (state === 'failed') {
          addLog('error', 'Prefill operation failed');
          setPrefillProgress(null);
        }
      });

      connection.onclose((error) => {
        console.log('Hub connection closed:', error);
        setIsConnecting(false);
      });

      connection.onreconnecting((error) => {
        console.log('Hub reconnecting:', error);
        addLog('warning', 'Connection lost, reconnecting...');
      });

      connection.onreconnected((connectionId) => {
        console.log('Hub reconnected:', connectionId);
        addLog('success', 'Reconnected to server');
        // Re-subscribe to session if we have one
        if (session) {
          connection.invoke('SubscribeToSession', session.id).catch(console.error);
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
  }, [session, onSessionEnd, handleAuthStateChanged, addLog]);

  const createSession = useCallback(async () => {
    setIsCreating(true);
    setError(null);
    setIsLoggedIn(false);
    setLogEntries([]); // Clear previous logs

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

      // Create session via hub
      const sessionDto = await connection.invoke<PrefillSessionDto>('CreateSession');
      setSession(sessionDto);
      setTimeRemaining(sessionDto.timeRemainingSeconds);

      addLog('success', 'Session created successfully', `Container: ${sessionDto.containerName}`);
      addLog('info', `Session expires in ${formatTimeRemaining(sessionDto.timeRemainingSeconds)}`);
      addLog('info', 'Click "Login to Steam" to authenticate before using prefill commands');

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
  }, [connectToHub, formatTimeRemaining, addLog]);

  const executeCommand = useCallback(async (commandType: CommandType) => {
    if (!session || !hubConnection.current) return;

    setIsExecuting(true);
    addLog('command', `Running: ${commandType}`);

    try {
      switch (commandType) {
        case 'select-apps': {
          // Get owned games list
          setIsLoadingGames(true);
          try {
            const games = await hubConnection.current.invoke('GetOwnedGames', session.id);
            setOwnedGames(games || []);
            addLog('info', `Found ${games?.length || 0} owned games`);
            setShowGameSelection(true);
          } finally {
            setIsLoadingGames(false);
          }
          break;
        }
        case 'select-status': {
          // Use local state - backend GetSelectedAppsStatus requires updated daemon
          if (selectedAppIds.length === 0) {
            addLog('warning', 'No games selected. Use "Select Apps" to choose games for prefill.');
          } else {
            const selectedNames = selectedAppIds.map(id => {
              const game = ownedGames.find(g => g.appId === id);
              return game ? game.name : `App ${id}`;
            });
            addLog('info', `${selectedAppIds.length} games selected for prefill:`);
            const displayNames = selectedNames.slice(0, 10);
            displayNames.forEach(name => addLog('info', `  • ${name}`));
            if (selectedNames.length > 10) {
              addLog('info', `  ... and ${selectedNames.length - 10} more`);
            }
          }
          break;
        }
        case 'prefill': {
          addLog('download', 'Starting prefill of selected apps...');
          const result = await hubConnection.current.invoke('StartPrefill', session.id, false, false, false);
          setPrefillProgress(null); // Clear progress on completion
          if (result?.success) {
            const totalSeconds = result.totalTime?.totalSeconds || 0;
            addLog('success', `Prefill completed in ${Math.round(totalSeconds)}s`);
          } else {
            addLog('error', result?.errorMessage || 'Prefill failed');
          }
          break;
        }
        case 'prefill-all': {
          addLog('download', 'Starting prefill of all owned games...');
          const result = await hubConnection.current.invoke('StartPrefill', session.id, true, false, false);
          setPrefillProgress(null); // Clear progress on completion
          if (result?.success) {
            const totalSeconds = result.totalTime?.totalSeconds || 0;
            addLog('success', `Prefill completed in ${Math.round(totalSeconds)}s`);
          } else {
            addLog('error', result?.errorMessage || 'Prefill failed');
          }
          break;
        }
        case 'prefill-recent': {
          addLog('download', 'Starting prefill of recently played games...');
          const result = await hubConnection.current.invoke('StartPrefill', session.id, false, true, false);
          setPrefillProgress(null); // Clear progress on completion
          if (result?.success) {
            const totalSeconds = result.totalTime?.totalSeconds || 0;
            addLog('success', `Prefill completed in ${Math.round(totalSeconds)}s`);
          } else {
            addLog('error', result?.errorMessage || 'Prefill failed');
          }
          break;
        }
        case 'prefill-force': {
          addLog('download', 'Starting force prefill (re-downloading)...');
          const result = await hubConnection.current.invoke('StartPrefill', session.id, false, false, true);
          setPrefillProgress(null); // Clear progress on completion
          if (result?.success) {
            const totalSeconds = result.totalTime?.totalSeconds || 0;
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
  }, [session, addLog, selectedAppIds, ownedGames]);

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

  const handleOpenAuthModal = useCallback(() => {
    authActions.resetAuthForm();
    setShowAuthModal(true);
  }, [authActions]);

  // Handle saving game selection
  const handleSaveGameSelection = useCallback(async (selectedIds: number[]) => {
    if (!session || !hubConnection.current) return;

    await hubConnection.current.invoke('SetSelectedApps', session.id, selectedIds);
    setSelectedAppIds(selectedIds);
    addLog('success', `Selected ${selectedIds.length} games for prefill`);
  }, [session, addLog]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      hubConnection.current?.stop();
    };
  }, []);

  return (
    <div className="space-y-4">
      {/* Steam Auth Modal */}
      <SteamAuthModal
        opened={showAuthModal}
        onClose={() => setShowAuthModal(false)}
        state={authState}
        actions={authActions}
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

      {/* Header Card */}
      <Card>
        <CardHeader className="flex flex-row items-center justify-between">
          <div className="flex items-center gap-2">
            <CardTitle>Steam Prefill</CardTitle>
          </div>
          {session && (
            <div className="flex items-center gap-4">
              <div className="flex items-center gap-2 text-sm text-muted-foreground">
                <Clock className="h-4 w-4" />
                <span>{formatTimeRemaining(timeRemaining)}</span>
              </div>
              <Button
                variant="outline"
                size="sm"
                onClick={handleEndSession}
                className="text-red-500 hover:text-red-600"
              >
                <X className="h-4 w-4 mr-1" />
                End Session
              </Button>
            </div>
          )}
        </CardHeader>
        <CardContent>
          {error && (
            <div className="mb-4 p-3 bg-red-500/10 border border-red-500/20 rounded-lg flex items-center gap-2 text-red-500">
              <AlertCircle className="h-4 w-4" />
              <span>{error}</span>
            </div>
          )}

          {!session && !isCreating && (
            <div className="flex flex-col items-center justify-center py-8 gap-4">
              <div className="text-center">
                <h3 className="text-lg font-medium">Steam Prefill</h3>
                <p className="text-sm text-muted-foreground mt-1">
                  Pre-download Steam games to your cache for faster LAN party downloads.
                </p>
                <p className="text-xs text-muted-foreground mt-2">
                  Requires Steam login to access your game library.
                </p>
              </div>
              <Button
                onClick={createSession}
                disabled={isConnecting || isCreating}
                size="lg"
              >
                {isConnecting ? (
                  <>
                    <Loader2 className="h-4 w-4 mr-2 animate-spin" />
                    Connecting...
                  </>
                ) : (
                  <>
                    <Play className="h-4 w-4 mr-2" />
                    Start Session
                  </>
                )}
              </Button>
            </div>
          )}

          {isCreating && !session && (
            <div className="flex items-center justify-center py-8 gap-3">
              <Loader2 className="h-6 w-6 animate-spin text-primary" />
              <span>Creating session...</span>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Command Buttons */}
      {session && (
        <Card>
          <CardHeader className="pb-3">
            <div className="flex items-center justify-between">
              <CardTitle className="text-base">Actions</CardTitle>
              <div className="flex items-center gap-2">
                {isLoggedIn ? (
                  <div className="flex items-center gap-1.5 text-sm text-green-500">
                    <CheckCircle2 className="h-4 w-4" />
                    <span>Logged In</span>
                  </div>
                ) : (
                  <Button
                    variant="filled"
                    size="sm"
                    onClick={handleOpenAuthModal}
                  >
                    <LogIn className="h-4 w-4 mr-1" />
                    Login to Steam
                  </Button>
                )}
              </div>
            </div>
          </CardHeader>
          <CardContent className="space-y-4">
            {!isLoggedIn && (
              <div className="p-3 bg-yellow-500/10 border border-yellow-500/20 rounded-lg text-sm">
                <div className="flex items-start gap-2">
                  <LogIn className="h-4 w-4 mt-0.5 text-yellow-500" />
                  <div>
                    <p className="text-yellow-500 font-medium">Login Required</p>
                    <p className="text-muted-foreground text-xs mt-1">
                      Click "Login to Steam" above to authenticate before using commands that require login.
                      Your credentials are sent directly to the container and never stored by this application.
                    </p>
                  </div>
                </div>
              </div>
            )}

            <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-2">
              {COMMAND_BUTTONS.map((cmd) => (
                <Button
                  key={cmd.id}
                  variant={cmd.variant || 'default'}
                  onClick={() => executeCommand(cmd.id)}
                  disabled={isExecuting || (cmd.requiresLogin && !isLoggedIn)}
                  className="flex flex-col h-auto py-2 px-3 text-left"
                  size="sm"
                >
                  <div className="flex items-center gap-1.5 w-full">
                    {cmd.icon}
                    <span className="font-medium text-xs">{cmd.label}</span>
                  </div>
                  <span className="text-[10px] opacity-60 w-full">{cmd.description}</span>
                </Button>
              ))}
            </div>
          </CardContent>
        </Card>
      )}

      {/* Prefill Progress */}
      {prefillProgress && (
        <Card>
          <CardHeader className="pb-2">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <Download className="h-4 w-4 text-primary animate-pulse" />
                <CardTitle className="text-base">Downloading</CardTitle>
              </div>
              <span className="text-sm text-muted-foreground">
                {formatBytes(prefillProgress.bytesPerSecond)}/s
              </span>
            </div>
          </CardHeader>
          <CardContent className="space-y-3">
            <div>
              <div className="flex items-center justify-between mb-1">
                <span className="text-sm font-medium truncate max-w-[60%]">
                  {prefillProgress.currentAppName || `App ${prefillProgress.currentAppId}`}
                </span>
                <span className="text-sm text-muted-foreground">
                  {prefillProgress.percentComplete.toFixed(1)}%
                </span>
              </div>
              <div className="h-2 bg-muted rounded-full overflow-hidden">
                <div
                  className="h-full bg-primary transition-all duration-300 ease-out"
                  style={{ width: `${Math.min(100, prefillProgress.percentComplete)}%` }}
                />
              </div>
              <div className="flex items-center justify-between mt-1 text-xs text-muted-foreground">
                <span>
                  {formatBytes(prefillProgress.bytesDownloaded)} / {formatBytes(prefillProgress.totalBytes)}
                </span>
                <span>
                  Elapsed: {formatTimeRemaining(Math.floor(prefillProgress.elapsedSeconds))}
                </span>
              </div>
            </div>
          </CardContent>
        </Card>
      )}

      {/* Activity Log */}
      {(session || isCreating) && (
        <Card>
          <CardHeader className="pb-2">
            <div className="flex items-center gap-2">
              <ScrollText className="h-4 w-4" />
              <CardTitle className="text-base">Activity Log</CardTitle>
            </div>
            <p className="text-xs text-muted-foreground">
              Status updates and command output
            </p>
          </CardHeader>
          <CardContent className="p-0 pb-4 px-4">
            <ActivityLog entries={logEntries} maxHeight="400px" />
          </CardContent>
        </Card>
      )}
    </div>
  );
}
