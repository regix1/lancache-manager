import React, {
  createContext,
  useContext,
  useEffect,
  useRef,
  useState,
  useCallback,
  useMemo
} from 'react';
import * as signalR from '@microsoft/signalr';
import { SIGNALR_BASE } from '@utils/constants';
import type { SignalRContextType, SignalRProviderProps, EventHandler } from './types';
// eslint-disable-next-line no-duplicate-imports
import { SIGNALR_EVENTS } from './types';
import authService from '@services/auth.service';

const SignalRContext = createContext<SignalRContextType | undefined>(undefined);

export const useSignalR = () => {
  const context = useContext(SignalRContext);
  if (!context) {
    throw new Error('useSignalR must be used within SignalRProvider');
  }
  return context;
};

export const SignalRProvider: React.FC<SignalRProviderProps> = ({ children, mockMode = false }) => {
  const [isConnected, setIsConnected] = useState(false);
  const [connectionState, setConnectionState] = useState<
    'disconnected' | 'connecting' | 'connected' | 'reconnecting'
  >('disconnected');
  const [connectionId, setConnectionId] = useState<string | null>(null);

  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const reconnectTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const isMountedRef = useRef(true);
  const mockModeRef = useRef(mockMode);
  const isSettingUpRef = useRef(false);
  // Track if we've already initialized to prevent double-mounting in Strict Mode
  const hasInitializedRef = useRef(false);
  // Track page visibility state
  const isPageVisibleRef = useRef(!document.hidden);
  // Track consecutive reconnection failures for exponential backoff
  const reconnectAttemptsRef = useRef(0);

  // Store event handlers - using Map for better performance
  const eventHandlersRef = useRef<Map<string, Set<EventHandler>>>(new Map());

  // Update mock mode ref when it changes
  useEffect(() => {
    mockModeRef.current = mockMode;
  }, [mockMode]);

  // Subscribe to an event
  const on = useCallback((eventName: string, handler: EventHandler) => {
    if (!eventHandlersRef.current.has(eventName)) {
      eventHandlersRef.current.set(eventName, new Set());
    }
    eventHandlersRef.current.get(eventName)!.add(handler);
  }, []);

  // Unsubscribe from an event
  const off = useCallback((eventName: string, handler: EventHandler) => {
    const handlers = eventHandlersRef.current.get(eventName);
    if (handlers) {
      handlers.delete(handler);
      if (handlers.size === 0) {
        eventHandlersRef.current.delete(eventName);
      }
    }
  }, []);

  // Invoke a hub method
  const invoke = useCallback(async (methodName: string, ...args: unknown[]) => {
    if (
      connectionRef.current &&
      connectionRef.current.state === signalR.HubConnectionState.Connected
    ) {
      try {
        await connectionRef.current.invoke(methodName, ...args);
      } catch (error) {
        console.error(`[SignalR] Error invoking ${methodName}:`, error);
        throw error;
      }
    } else {
      console.warn(`[SignalR] Cannot invoke ${methodName}: not connected`);
    }
  }, []);

  // Calculate backoff delay with exponential increase and jitter
  const getReconnectDelay = useCallback(() => {
    const baseDelay = 2000; // 2 seconds
    const maxDelay = 60000; // 60 seconds max
    const attempts = reconnectAttemptsRef.current;

    // Exponential backoff: 2s, 4s, 8s, 16s, 32s, 60s (capped)
    const exponentialDelay = Math.min(baseDelay * Math.pow(2, attempts), maxDelay);

    // Add jitter (±25%) to prevent thundering herd
    const jitter = exponentialDelay * 0.25 * (Math.random() * 2 - 1);

    return Math.round(exponentialDelay + jitter);
  }, []);

  // Setup SignalR connection
  const setupConnection = useCallback(async () => {
    // Don't connect in mock mode
    if (mockModeRef.current) {
      return;
    }

    // Don't connect if page is hidden - wait until visible
    if (!isPageVisibleRef.current) {
      return;
    }

    // Don't connect without a valid authenticated session token.
    // This avoids repeated unauthenticated negotiate/handshake churn.
    if (!authService.getSessionToken()) {
      if (reconnectTimeoutRef.current) {
        clearTimeout(reconnectTimeoutRef.current);
        reconnectTimeoutRef.current = null;
      }
      if (isMountedRef.current) {
        setConnectionState('disconnected');
        setIsConnected(false);
        setConnectionId(null);
      }
      return;
    }

    // Prevent concurrent setup attempts (happens during React Strict Mode double mount)
    if (isSettingUpRef.current) {
      return;
    }

    // Check if we already have a valid connection
    if (
      connectionRef.current &&
      (connectionRef.current.state === signalR.HubConnectionState.Connected ||
        connectionRef.current.state === signalR.HubConnectionState.Connecting)
    ) {
      return;
    }

    isSettingUpRef.current = true;

    // Stop any existing connection first
    if (connectionRef.current) {
      try {
        await connectionRef.current.stop();
      } catch (err) {
        console.warn('[SignalR] Error stopping existing connection:', err);
      }
      connectionRef.current = null;
    }

    try {
      setConnectionState('connecting');

      const connection = new signalR.HubConnectionBuilder()
        .withUrl(`${SIGNALR_BASE}/downloads`, {
          withCredentials: true,
          accessTokenFactory: () => authService.getSessionToken() || ''
        })
        .withAutomaticReconnect({
          nextRetryDelayInMilliseconds: (retryContext) => {
            // Don't auto-reconnect if page is hidden
            if (!isPageVisibleRef.current) {
              return null; // Stop auto-reconnect, will reconnect when page becomes visible
            }
            // Progressive backoff: 0ms, 2s, 5s, 10s, 30s, then 30s
            if (retryContext.previousRetryCount === 0) return 0;
            if (retryContext.previousRetryCount === 1) return 2000;
            if (retryContext.previousRetryCount === 2) return 5000;
            if (retryContext.previousRetryCount === 3) return 10000;
            if (retryContext.previousRetryCount > 10) return null; // Give up after 10 attempts
            return 30000;
          }
        })
        // Increase timeout to prevent disconnections during heavy processing
        // Must match server settings: KeepAliveInterval=10s, ClientTimeoutInterval=60s
        .withServerTimeout(60000) // 60 seconds (default: 30 seconds)
        .withKeepAliveInterval(10000) // 10 seconds (default: 15 seconds)
        .configureLogging(signalR.LogLevel.Warning)
        .build();

      // Set up connection lifecycle handlers
      connection.onreconnecting((_error) => {
        if (isMountedRef.current) {
          setConnectionState('reconnecting');
          setIsConnected(false);
        }
      });

      connection.onreconnected((connectionId) => {
        if (isMountedRef.current) {
          setConnectionState('connected');
          setIsConnected(true);
          setConnectionId(connectionId || null);
          // Reset reconnect attempts on successful reconnection
          reconnectAttemptsRef.current = 0;
        }
      });

      connection.onclose((_error) => {
        if (isMountedRef.current) {
          setConnectionState('disconnected');
          setIsConnected(false);
          setConnectionId(null);

          // Don't auto-reconnect if in mock mode or component unmounted
          if (mockModeRef.current || !isMountedRef.current) {
            return;
          }

          // Don't reconnect if page is hidden - wait until visible
          if (!isPageVisibleRef.current) {
            return;
          }

          // Don't reconnect while unauthenticated
          if (!authService.getSessionToken()) {
            return;
          }

          // Clear any pending reconnection
          if (reconnectTimeoutRef.current) {
            clearTimeout(reconnectTimeoutRef.current);
          }

          // Calculate delay with exponential backoff
          const delay = getReconnectDelay();
          reconnectAttemptsRef.current++;

          reconnectTimeoutRef.current = setTimeout(() => {
            if (isMountedRef.current && !mockModeRef.current && isPageVisibleRef.current) {
              setupConnection();
            }
          }, delay);
        }
      });

      // Set up event dispatchers - these dispatch to our handlers map
      const setupEventDispatchers = () => {
        SIGNALR_EVENTS.forEach((eventName) => {
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          connection.on(eventName, (...args: any[]) => {
            // Dispatch to all registered handlers for this event
            const handlers = eventHandlersRef.current.get(eventName);

            if (handlers && handlers.size > 0) {
              handlers.forEach((handler) => {
                try {
                  handler(...args);
                } catch (error) {
                  console.error(`[SignalR] Error in handler for ${eventName}:`, error);
                }
              });
            }
          });
        });
      };

      setupEventDispatchers();

      // Start the connection
      await connection.start();

      if (isMountedRef.current) {
        connectionRef.current = connection;
        setConnectionState('connected');
        setIsConnected(true);
        setConnectionId(connection.connectionId || null);
        // Reset reconnect attempts on successful connection
        reconnectAttemptsRef.current = 0;
        isSettingUpRef.current = false;
      } else {
        // Component unmounted while connecting, clean up
        await connection.stop();
        isSettingUpRef.current = false;
      }
    } catch (error) {
      console.error('[SignalR] Connection failed:', error);
      isSettingUpRef.current = false;
      if (isMountedRef.current) {
        setConnectionState('disconnected');
        setIsConnected(false);
        setConnectionId(null);

        // Don't retry if in mock mode or component unmounted or page hidden
        if (mockModeRef.current || !isMountedRef.current || !isPageVisibleRef.current) {
          return;
        }

        // Don't retry while unauthenticated
        if (!authService.getSessionToken()) {
          return;
        }

        // Clear any pending reconnection
        if (reconnectTimeoutRef.current) {
          clearTimeout(reconnectTimeoutRef.current);
        }

        // Calculate delay with exponential backoff
        const delay = getReconnectDelay();
        reconnectAttemptsRef.current++;

        reconnectTimeoutRef.current = setTimeout(() => {
          if (isMountedRef.current && !mockModeRef.current && isPageVisibleRef.current) {
            setupConnection();
          }
        }, delay);
      }
    }
  }, [getReconnectDelay]);

  // Respond to auth session changes by connecting only when authenticated
  // and disconnecting immediately when auth/session is cleared.
  useEffect(() => {
    if (mockMode) {
      return;
    }

    const handleAuthSessionUpdated = () => {
      const hasToken = Boolean(authService.getSessionToken());

      if (!hasToken) {
        reconnectAttemptsRef.current = 0;

        if (reconnectTimeoutRef.current) {
          clearTimeout(reconnectTimeoutRef.current);
          reconnectTimeoutRef.current = null;
        }

        const activeConnection = connectionRef.current;
        connectionRef.current = null;
        if (activeConnection) {
          activeConnection.stop().catch((err) => {
            console.error('[SignalR] Error stopping connection after auth clear:', err);
          });
        }

        if (isMountedRef.current) {
          setConnectionState('disconnected');
          setIsConnected(false);
          setConnectionId(null);
        }
        return;
      }

      reconnectAttemptsRef.current = 0;
      if (isPageVisibleRef.current) {
        setupConnection();
      }
    };

    window.addEventListener('auth-session-updated', handleAuthSessionUpdated);
    return () => {
      window.removeEventListener('auth-session-updated', handleAuthSessionUpdated);
    };
  }, [mockMode, setupConnection]);

  // Handle page visibility changes
  useEffect(() => {
    if (mockMode) return;

    const handleVisibilityChange = () => {
      const wasVisible = isPageVisibleRef.current;
      isPageVisibleRef.current = !document.hidden;

      if (!wasVisible && isPageVisibleRef.current) {
        // Page became visible
        // Check if we need to reconnect
        if (
          !connectionRef.current ||
          connectionRef.current.state === signalR.HubConnectionState.Disconnected
        ) {
          // Reset backoff since this is a user-initiated visibility change
          reconnectAttemptsRef.current = 0;
          // Clear any pending reconnection and connect immediately
          if (reconnectTimeoutRef.current) {
            clearTimeout(reconnectTimeoutRef.current);
            reconnectTimeoutRef.current = null;
          }
          setupConnection();
        }
      }
      // When page becomes hidden, we let the connection stay open
      // The server timeout and browser's WebSocket handling will manage it
      // We just won't try to reconnect while hidden
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);

    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, [mockMode, setupConnection]);

  // Initialize connection on mount
  useEffect(() => {
    isMountedRef.current = true;
    let connectionStartTimeout: NodeJS.Timeout | null = null;
    const eventHandlers = eventHandlersRef.current;

    // Don't connect in mock mode
    if (mockMode) {
      setConnectionState('disconnected');
      setIsConnected(false);
      return;
    }

    // Prevent duplicate connections during React Strict Mode double-mount
    if (hasInitializedRef.current) {
      return;
    }

    // Mark as initialized before connecting
    hasInitializedRef.current = true;
    connectionStartTimeout = setTimeout(() => {
      setupConnection();
    }, 0);

    // Cleanup function
    return () => {
      if (connectionStartTimeout) {
        clearTimeout(connectionStartTimeout);
        connectionStartTimeout = null;
      }

      isMountedRef.current = false;

      // Clear any pending reconnection attempts
      if (reconnectTimeoutRef.current) {
        clearTimeout(reconnectTimeoutRef.current);
        reconnectTimeoutRef.current = null;
      }

      // Stop the connection if it exists
      if (connectionRef.current) {
        const connToStop = connectionRef.current;
        connectionRef.current = null;
        connToStop.stop().catch((err) => {
          console.error('[SignalR] Error stopping connection:', err);
        });
      }

      // Clear all event handlers
      eventHandlers.clear();

      // Reset setup flag only (keep hasInitializedRef to prevent duplicate connections)
      isSettingUpRef.current = false;
    };
  }, [mockMode, setupConnection]);

  // Memoize the context value to prevent unnecessary re-renders
  const value: SignalRContextType = useMemo(
    () => ({
      isConnected,
      connectionState,
      on,
      off,
      invoke,
      connectionId
    }),
    [isConnected, connectionState, on, off, invoke, connectionId]
  );

  return <SignalRContext.Provider value={value}>{children}</SignalRContext.Provider>;
};
