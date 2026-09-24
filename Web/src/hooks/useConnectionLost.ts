import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useMockMode } from '@contexts/useMockMode';
import { useAuth } from '@contexts/useAuth';

/**
 * True while a browser with a session (signed in or guest) has lost its live connection to the
 * server. With no session no socket is ever opened (SignalRContext/index.tsx, the no-session path of
 * setupConnection) and mock mode opens none, so neither counts as lost.
 */
export function useConnectionLost(): boolean {
  const { connectionState } = useSignalR();
  const { mockMode } = useMockMode();
  const { hasSession } = useAuth();
  return (
    hasSession &&
    !mockMode &&
    (connectionState === 'disconnected' || connectionState === 'reconnecting')
  );
}
