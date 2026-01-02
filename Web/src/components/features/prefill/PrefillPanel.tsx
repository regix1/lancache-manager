import { useEffect, useRef, useCallback, useState } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Card, CardContent, CardHeader, CardTitle } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { usePrefillSteamAuth } from '@hooks/usePrefillSteamAuth';
import authService from '@services/auth.service';
import { SIGNALR_BASE } from '@utils/constants';
import {
    Loader2,
    Terminal as TerminalIcon,
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
    const terminalRef = useRef<HTMLDivElement>(null);
    const terminalInstance = useRef<Terminal | null>(null);
    const fitAddon = useRef<FitAddon | null>(null);
    const hubConnection = useRef<HubConnection | null>(null);
    const resizeObserver = useRef<ResizeObserver | null>(null);

    const [session, setSession] = useState<PrefillSessionDto | null>(null);
    const [isConnecting, setIsConnecting] = useState(false);
    const [isCreating, setIsCreating] = useState(false);
    const [isExecuting, setIsExecuting] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [timeRemaining, setTimeRemaining] = useState<number>(0);
    const [showAuthModal, setShowAuthModal] = useState(false);
    const [isLoggedIn, setIsLoggedIn] = useState(false);

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
                break;
            case 'CredentialsRequired':
                authActions.resetAuthForm();
                setShowAuthModal(true);
                break;
            case 'TwoFactorRequired':
                trigger2FAPrompt();
                setShowAuthModal(true);
                break;
            case 'EmailCodeRequired':
                triggerEmailPrompt();
                setShowAuthModal(true);
                break;
            case 'NotAuthenticated':
                setIsLoggedIn(false);
                break;
        }
    }, [authActions, trigger2FAPrompt, triggerEmailPrompt]);

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

    const initializeTerminal = useCallback((sessionDto: PrefillSessionDto) => {
        if (!terminalRef.current || terminalInstance.current) return;

        const terminal = new Terminal({
            cursorBlink: false, // No cursor blink since input is disabled
            fontSize: 13,
            fontFamily: 'Menlo, Monaco, "Courier New", monospace',
            theme: {
                background: '#1a1b26',
                foreground: '#a9b1d6',
                cursor: '#1a1b26', // Hide cursor
                cursorAccent: '#1a1b26',
                selectionBackground: '#33467c',
                black: '#414868',
                red: '#f7768e',
                green: '#9ece6a',
                yellow: '#e0af68',
                blue: '#7aa2f7',
                magenta: '#bb9af7',
                cyan: '#7dcfff',
                white: '#c0caf5',
                brightBlack: '#414868',
                brightRed: '#f7768e',
                brightGreen: '#9ece6a',
                brightYellow: '#e0af68',
                brightBlue: '#7aa2f7',
                brightMagenta: '#bb9af7',
                brightCyan: '#7dcfff',
                brightWhite: '#c0caf5',
            },
            scrollback: 10000,
            allowProposedApi: true,
            disableStdin: true, // Disable keyboard input - use modal instead
        });

        const fit = new FitAddon();
        terminal.loadAddon(fit);
        terminal.open(terminalRef.current);
        fit.fit();

        terminalInstance.current = terminal;
        fitAddon.current = fit;

        // Handle resize
        resizeObserver.current = new ResizeObserver(() => {
            if (fitAddon.current) {
                fitAddon.current.fit();
                // Notify backend of resize
                if (hubConnection.current && sessionDto) {
                    const cols = terminal.cols;
                    const rows = terminal.rows;
                    hubConnection.current.invoke('ResizeTerminal', sessionDto.id, cols, rows)
                        .catch(err => console.error('Failed to resize:', err));
                }
            }
        });
        resizeObserver.current.observe(terminalRef.current);

        return terminal;
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
                .withUrl(`${SIGNALR_BASE}/prefill?deviceId=${encodeURIComponent(deviceId)}`)
                .withAutomaticReconnect()
                .configureLogging(LogLevel.Information)
                .build();

            // Handle terminal output from container
            connection.on('TerminalOutput', (_sessionId: string, output: string) => {
                if (terminalInstance.current) {
                    terminalInstance.current.write(output);
                }
            });

            // Handle auth state changes from backend
            connection.on('AuthStateChanged', (_sessionId: string, newState: SteamAuthState) => {
                handleAuthStateChanged(newState);
            });

            // Handle session attached confirmation
            connection.on('SessionAttached', (sessionDto: PrefillSessionDto) => {
                setSession(sessionDto);
                setTimeRemaining(sessionDto.timeRemainingSeconds);
                // Initialize login state from session auth state
                setIsLoggedIn(sessionDto.authState === 'Authenticated');
            });

            // Handle session ended
            connection.on('SessionEnded', (_sessionId: string, reason: string) => {
                if (terminalInstance.current) {
                    terminalInstance.current.writeln(`\r\n\x1b[33m[Session ended: ${reason}]\x1b[0m`);
                }
                setSession(null);
                setIsExecuting(false);
                setIsLoggedIn(false);
                onSessionEnd?.();
            });

            connection.onclose((error) => {
                console.log('Hub connection closed:', error);
                setIsConnecting(false);
            });

            connection.onreconnecting((error) => {
                console.log('Hub reconnecting:', error);
                if (terminalInstance.current) {
                    terminalInstance.current.writeln('\r\n\x1b[33m[Reconnecting...]\x1b[0m');
                }
            });

            connection.onreconnected((connectionId) => {
                console.log('Hub reconnected:', connectionId);
                if (terminalInstance.current) {
                    terminalInstance.current.writeln('\r\n\x1b[32m[Reconnected]\x1b[0m');
                }
                // Re-attach to session if we have one
                if (session) {
                    connection.invoke('AttachSession', session.id).catch(console.error);
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
    }, [session, onSessionEnd, handleAuthStateChanged]);

    const createSession = useCallback(async () => {
        setIsCreating(true);
        setError(null);
        setIsLoggedIn(false);

        try {
            // Connect to hub if not connected
            let connection = hubConnection.current;
            if (!connection || connection.state !== 'Connected') {
                connection = await connectToHub();
            }

            if (!connection) {
                throw new Error('Failed to establish connection');
            }

            // Create session via hub
            const sessionDto = await connection.invoke<PrefillSessionDto>('CreateSession');
            setSession(sessionDto);
            setTimeRemaining(sessionDto.timeRemainingSeconds);

            // Initialize terminal after we have the session
            const terminal = initializeTerminal(sessionDto);
            if (!terminal) {
                throw new Error('Failed to initialize terminal');
            }

            terminal.writeln('\x1b[32mSession created successfully!\x1b[0m');
            terminal.writeln(`\x1b[90mContainer: ${sessionDto.containerName}\x1b[0m`);
            terminal.writeln(`\x1b[90mSession expires in ${formatTimeRemaining(sessionDto.timeRemainingSeconds)}\x1b[0m\n`);
            terminal.writeln('\x1b[36mUse the buttons above to run commands.\x1b[0m');
            terminal.writeln('\x1b[36mClick "Login to Steam" to authenticate before using commands that require login.\x1b[0m\n');

            // Attach to session to start receiving output
            await connection.invoke('AttachSession', sessionDto.id);

            setIsCreating(false);
        } catch (err) {
            console.error('Failed to create session:', err);
            const errorMessage = err instanceof Error ? err.message : 'Failed to create session';
            setError(errorMessage);
            if (terminalInstance.current) {
                terminalInstance.current.writeln(`\r\n\x1b[31mError: ${errorMessage}\x1b[0m`);
            }
            setIsCreating(false);
        }
    }, [connectToHub, initializeTerminal, formatTimeRemaining]);

    const executeCommand = useCallback(async (commandType: CommandType, options?: Record<string, string>) => {
        if (!session || !hubConnection.current) return;

        setIsExecuting(true);

        try {
            if (terminalInstance.current) {
                terminalInstance.current.writeln(`\r\n\x1b[36m> Running: ${commandType}\x1b[0m\n`);
            }

            await hubConnection.current.invoke('ExecuteCommand', session.id, commandType, options || null);
        } catch (err) {
            console.error('Failed to execute command:', err);
            const errorMessage = err instanceof Error ? err.message : 'Failed to execute command';
            if (terminalInstance.current) {
                terminalInstance.current.writeln(`\r\n\x1b[31mError: ${errorMessage}\x1b[0m`);
            }
        } finally {
            setTimeout(() => setIsExecuting(false), 1000);
        }
    }, [session]);

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

    // Cleanup on unmount
    useEffect(() => {
        return () => {
            resizeObserver.current?.disconnect();
            terminalInstance.current?.dispose();
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

            {/* Output Terminal */}
            {(session || isCreating) && (
                <Card>
                    <CardHeader className="pb-2">
                        <div className="flex items-center gap-2">
                            <TerminalIcon className="h-4 w-4" />
                            <CardTitle className="text-base">Output</CardTitle>
                        </div>
                        <p className="text-xs text-muted-foreground">
                            Command output will appear here
                        </p>
                    </CardHeader>
                    <CardContent className="p-0">
                        <div
                            ref={terminalRef}
                            className="w-full rounded-b-lg overflow-hidden h-[400px]"
                            style={{ backgroundColor: '#1a1b26' }}
                        />
                    </CardContent>
                </Card>
            )}
        </div>
    );
}
