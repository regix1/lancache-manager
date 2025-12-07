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
import { SIGNALR_EVENTS } from './types';

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

  // Store event handlers - using Map for better performance
  const eventHandlersRef = useRef<Map<string, Set<EventHandler>>>(new Map());

  // Update mock mode ref when it changes
  useEffect(() => {
    mockModeRef.current = mockMode;
  }, [mockMode]);

  // Subscribe to an event
  const on = useCallback((eventName: string, handler: EventHandler) => {
    // console.log(`[SignalR] Registering handler for event: ${eventName}`);

    if (!eventHandlersRef.current.has(eventName)) {
      eventHandlersRef.current.set(eventName, new Set());
    }
    eventHandlersRef.current.get(eventName)!.add(handler);

    // console.log(`[SignalR] Handler registered. Total handlers for ${eventName}: ${eventHandlersRef.current.get(eventName)?.size || 0}`);

    // If connection exists and is connected, add the handler to SignalR
    if (
      connectionRef.current &&
      connectionRef.current.state === signalR.HubConnectionState.Connected
    ) {
      // SignalR handlers are already set up to dispatch to our handlers map
      // No need to call connection.on again
    }
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

  // Setup SignalR connection
  const setupConnection = useCallback(async () => {
    // Don't connect in mock mode
    if (mockModeRef.current) {
      // console.log('[SignalR] Skipping connection in mock mode');
      return;
    }

    // Prevent concurrent setup attempts (happens during React Strict Mode double mount)
    if (isSettingUpRef.current) {
      // console.log('[SignalR] Setup already in progress, skipping');
      return;
    }

    // Check if we already have a valid connection
    if (
      connectionRef.current &&
      (connectionRef.current.state === signalR.HubConnectionState.Connected ||
        connectionRef.current.state === signalR.HubConnectionState.Connecting)
    ) {
      // console.log('[SignalR] Connection already exists (state:', connectionRef.current.state, '), skipping setup');
      return;
    }

    isSettingUpRef.current = true;

    // Stop any existing connection first
    if (connectionRef.current) {
      // const existingState = connectionRef.current.state;
      // console.log('[SignalR] Stopping existing connection (state:', existingState, ')');
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
          withCredentials: true // Important: include session cookies
        })
        .withAutomaticReconnect({
          nextRetryDelayInMilliseconds: (retryContext) => {
            // Progressive backoff: 0ms, 2s, 5s, 10s, 30s, then 30s
            if (retryContext.previousRetryCount === 0) return 0;
            if (retryContext.previousRetryCount === 1) return 2000;
            if (retryContext.previousRetryCount === 2) return 5000;
            if (retryContext.previousRetryCount === 3) return 10000;
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
        // console.log('[SignalR] Reconnecting...', error);
        if (isMountedRef.current) {
          setConnectionState('reconnecting');
          setIsConnected(false);
        }
      });

      connection.onreconnected((connectionId) => {
        // console.log('[SignalR] Reconnected successfully, ID:', connectionId);
        if (isMountedRef.current) {
          setConnectionState('connected');
          setIsConnected(true);
          setConnectionId(connectionId || null);
        }
      });

      connection.onclose((_error) => {
        // console.log('[SignalR] Connection closed', error);
        if (isMountedRef.current) {
          setConnectionState('disconnected');
          setIsConnected(false);
          setConnectionId(null);

          // Don't auto-reconnect if in mock mode or component unmounted
          if (mockModeRef.current || !isMountedRef.current) {
            return;
          }

          // Attempt to reconnect after 5 seconds
          if (reconnectTimeoutRef.current) {
            clearTimeout(reconnectTimeoutRef.current);
          }

          reconnectTimeoutRef.current = setTimeout(() => {
            if (isMountedRef.current && !mockModeRef.current) {
              // console.log('[SignalR] Attempting manual reconnection...');
              setupConnection();
            }
          }, 5000);
        }
      });

      // Set up event dispatchers - these dispatch to our handlers map
      const setupEventDispatchers = () => {
        SIGNALR_EVENTS.forEach((eventName) => {
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          connection.on(eventName, (...args: any[]) => {
            // console.log(`[SignalR] Received event: ${eventName}`, args);

            // Dispatch to all registered handlers for this event
            const handlers = eventHandlersRef.current.get(eventName);
            // console.log(`[SignalR] Handlers for ${eventName}:`, handlers?.size || 0);

            if (handlers && handlers.size > 0) {
              handlers.forEach((handler) => {
                try {
                  handler(...args);
                } catch (error) {
                  console.error(`[SignalR] Error in handler for ${eventName}:`, error);
                }
              });
            }
            // Removed: No need to warn about unregistered handlers - it's normal
          });
        });
      };

      setupEventDispatchers();

      // Start the connection
      await connection.start();

      // console.log('[SignalR] Connected successfully, ID:', connection.connectionId);

      if (isMountedRef.current) {
        connectionRef.current = connection;
        setConnectionState('connected');
        setIsConnected(true);
        setConnectionId(connection.connectionId || null);
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
        isSettingUpRef.current = false;

        // Don't retry if in mock mode or component unmounted
        if (mockModeRef.current || !isMountedRef.current) {
          return;
        }

        // Retry after 5 seconds
        if (reconnectTimeoutRef.current) {
          clearTimeout(reconnectTimeoutRef.current);
        }

        reconnectTimeoutRef.current = setTimeout(() => {
          if (isMountedRef.current && !mockModeRef.current) {
            // console.log('[SignalR] Retrying connection...');
            setupConnection();
          }
        }, 5000);
      } else {
        isSettingUpRef.current = false;
      }
    }
  }, []);

  // Initialize connection on mount
  useEffect(() => {
    isMountedRef.current = true;

    // Don't connect in mock mode
    if (mockMode) {
      // console.log('[SignalR] Mock mode enabled, not connecting');
      setConnectionState('disconnected');
      setIsConnected(false);
      return;
    }

    // Prevent duplicate connections during React Strict Mode double-mount
    if (hasInitializedRef.current) {
      // console.log('[SignalR] Already initialized, skipping duplicate connection attempt');
      return;
    }

    // Mark as initialized before connecting
    hasInitializedRef.current = true;
    setupConnection();

    // Cleanup function
    return () => {
      isMountedRef.current = false;

      // Clear any pending reconnection attempts
      if (reconnectTimeoutRef.current) {
        clearTimeout(reconnectTimeoutRef.current);
        reconnectTimeoutRef.current = null;
      }

      // Stop the connection if it exists
      if (connectionRef.current) {
        // console.log('[SignalR] Stopping connection on unmount');
        const connToStop = connectionRef.current;
        connectionRef.current = null;
        connToStop.stop().catch((err) => {
          console.error('[SignalR] Error stopping connection:', err);
        });
      }

      // Clear all event handlers
      eventHandlersRef.current.clear();

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
      connectionId
    }),
    [isConnected, connectionState, on, off, connectionId]
  );

  return <SignalRContext.Provider value={value}>{children}</SignalRContext.Provider>;
};
