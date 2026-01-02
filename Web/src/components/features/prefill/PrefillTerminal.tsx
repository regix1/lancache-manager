import { useEffect, useRef, useCallback, useState } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { WebLinksAddon } from '@xterm/addon-web-links';
import '@xterm/xterm/css/xterm.css';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Card, CardContent, CardHeader, CardTitle } from '../../ui/Card';
import { Button } from '../../ui/Button';
import authService from '@services/auth.service';
import { SIGNALR_BASE } from '@utils/constants';
import { Loader2, Terminal as TerminalIcon, X, Clock, AlertCircle } from 'lucide-react';

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
}

interface PrefillTerminalProps {
    onSessionEnd?: () => void;
}

export function PrefillTerminal({ onSessionEnd }: PrefillTerminalProps) {
    const terminalRef = useRef<HTMLDivElement>(null);
    const terminalInstance = useRef<Terminal | null>(null);
    const fitAddon = useRef<FitAddon | null>(null);
    const hubConnection = useRef<HubConnection | null>(null);
    const resizeObserver = useRef<ResizeObserver | null>(null);

    const [session, setSession] = useState<PrefillSessionDto | null>(null);
    const [isConnecting, setIsConnecting] = useState(false);
    const [isCreating, setIsCreating] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [timeRemaining, setTimeRemaining] = useState<number>(0);

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

    const initializeTerminal = useCallback(() => {
        if (!terminalRef.current || terminalInstance.current) return;

        const terminal = new Terminal({
            cursorBlink: true,
            fontSize: 14,
            fontFamily: 'Menlo, Monaco, "Courier New", monospace',
            theme: {
                background: '#1a1b26',
                foreground: '#a9b1d6',
                cursor: '#c0caf5',
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
        });

        const fit = new FitAddon();
        const webLinks = new WebLinksAddon();

        terminal.loadAddon(fit);
        terminal.loadAddon(webLinks);
        terminal.open(terminalRef.current);
        fit.fit();

        terminalInstance.current = terminal;
        fitAddon.current = fit;

        // Handle resize
        resizeObserver.current = new ResizeObserver(() => {
            if (fitAddon.current) {
                fitAddon.current.fit();
                // Notify backend of resize
                if (hubConnection.current && session) {
                    hubConnection.current.invoke('ResizeTerminal', session.id, terminal.cols, terminal.rows)
                        .catch(err => console.error('Failed to resize terminal:', err));
                }
            }
        });
        resizeObserver.current.observe(terminalRef.current);

        return terminal;
    }, [session]);

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

            // Handle session attached confirmation
            connection.on('SessionAttached', (sessionDto: PrefillSessionDto) => {
                setSession(sessionDto);
                setTimeRemaining(sessionDto.timeRemainingSeconds);
            });

            // Handle session ended
            connection.on('SessionEnded', (_sessionId: string, reason: string) => {
                if (terminalInstance.current) {
                    terminalInstance.current.writeln(`\r\n\x1b[33m[Session ended: ${reason}]\x1b[0m`);
                }
                setSession(null);
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
    }, [session, onSessionEnd]);

    const createSession = useCallback(async () => {
        setIsCreating(true);
        setError(null);

        try {
            // Connect to hub if not connected
            let connection = hubConnection.current;
            if (!connection || connection.state !== 'Connected') {
                connection = await connectToHub();
            }

            if (!connection) {
                throw new Error('Failed to establish connection');
            }

            // Initialize terminal
            const terminal = initializeTerminal();
            if (!terminal) {
                throw new Error('Failed to initialize terminal');
            }

            terminal.writeln('\x1b[36mCreating Steam Prefill session...\x1b[0m');
            terminal.writeln('\x1b[90mThis may take a moment if the container image needs to be pulled.\x1b[0m\n');

            // Create session via hub
            const sessionDto = await connection.invoke<PrefillSessionDto>('CreateSession');
            setSession(sessionDto);
            setTimeRemaining(sessionDto.timeRemainingSeconds);

            terminal.writeln('\x1b[32mSession created successfully!\x1b[0m');
            terminal.writeln(`\x1b[90mContainer: ${sessionDto.containerName}\x1b[0m`);
            terminal.writeln(`\x1b[90mSession expires in ${formatTimeRemaining(sessionDto.timeRemainingSeconds)}\x1b[0m\n`);
            terminal.writeln('\x1b[36mTo get started, run:\x1b[0m');
            terminal.writeln('\x1b[33m  ./SteamPrefill select-apps\x1b[0m');
            terminal.writeln('\x1b[36mThen:\x1b[0m');
            terminal.writeln('\x1b[33m  ./SteamPrefill prefill\x1b[0m\n');

            // Attach to session to start receiving output
            await connection.invoke('AttachSession', sessionDto.id);

            // Handle terminal input
            terminal.onData((data) => {
                if (connection && sessionDto) {
                    connection.invoke('SendInput', sessionDto.id, data)
                        .catch(err => console.error('Failed to send input:', err));
                }
            });

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

    const handleEndSession = useCallback(async () => {
        if (!session || !hubConnection.current) return;

        try {
            await hubConnection.current.invoke('EndSession', session.id);
        } catch (err) {
            console.error('Failed to end session:', err);
        }

        setSession(null);
        onSessionEnd?.();
    }, [session, onSessionEnd]);

    // Cleanup on unmount
    useEffect(() => {
        return () => {
            resizeObserver.current?.disconnect();
            terminalInstance.current?.dispose();
            hubConnection.current?.stop();
        };
    }, []);

    return (
        <Card>
            <CardHeader className="flex flex-row items-center justify-between">
                <div className="flex items-center gap-2">
                    <TerminalIcon className="h-5 w-5" />
                    <CardTitle>Steam Prefill Terminal</CardTitle>
                </div>
                {session && (
                    <div className="flex items-center gap-4">
                        <div className="flex items-center gap-2 text-sm text-muted-foreground">
                            <Clock className="h-4 w-4" />
                            <span>Time remaining: {formatTimeRemaining(timeRemaining)}</span>
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
                    <div className="flex flex-col items-center justify-center py-12 gap-4">
                        <TerminalIcon className="h-16 w-16 text-muted-foreground" />
                        <div className="text-center">
                            <h3 className="text-lg font-medium">Steam Prefill</h3>
                            <p className="text-sm text-muted-foreground mt-1">
                                Start a terminal session to prefill your Steam games into the cache.
                            </p>
                            <p className="text-xs text-muted-foreground mt-2">
                                You will need to log in with your Steam account inside the terminal.
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
                                    <TerminalIcon className="h-4 w-4 mr-2" />
                                    Start Prefill Session
                                </>
                            )}
                        </Button>
                    </div>
                )}

                {isCreating && !session && (
                    <div className="flex items-center justify-center py-12 gap-3">
                        <Loader2 className="h-6 w-6 animate-spin text-primary" />
                        <span>Creating session...</span>
                    </div>
                )}

                <div
                    ref={terminalRef}
                    className={`w-full rounded-lg overflow-hidden ${session ? 'h-[500px]' : 'h-0'}`}
                    style={{ backgroundColor: '#1a1b26' }}
                />

                {session && (
                    <div className="mt-4 text-xs text-muted-foreground">
                        <p>
                            <strong>Tip:</strong> Run <code className="bg-muted px-1 rounded">./SteamPrefill select-apps</code> to choose games,
                            then <code className="bg-muted px-1 rounded">./SteamPrefill prefill</code> to start downloading.
                        </p>
                    </div>
                )}
            </CardContent>
        </Card>
    );
}
