import { useEffect, useRef } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from './useReconnectRefetch';

interface SignInEndingWait {
  attemptId: string;
  deadline: number;
}

/**
 * Waits for the ending of a sign-in whose answer this page lost, without asking the server on a timer: `read` runs when
 * the server announces that attempt's ending, when the hub connection comes back, and once at the attempt's own deadline
 * with `final` set, where a read that still decides nothing ends the attempt.
 */
export function useSignInEndingWait(
  wait: SignInEndingWait | null,
  read: (final: boolean) => void
): void {
  const { on, off, isConnected } = useSignalR();
  const readRef = useRef(read);
  readRef.current = read;
  const waitRef = useRef(wait);
  waitRef.current = wait;

  useEffect(() => {
    if (!wait) return;
    const handleEnded = ({ attemptId }: { attemptId: string }) => {
      if (attemptId === wait.attemptId) readRef.current(false);
    };
    on('IntegrationLoginEnded', handleEnded);
    const expire = setTimeout(() => readRef.current(true), Math.max(0, wait.deadline - Date.now()));
    return () => {
      off('IntegrationLoginEnded', handleEnded);
      clearTimeout(expire);
    };
  }, [wait, on, off]);

  useReconnectRefetch(isConnected, () => {
    if (waitRef.current) readRef.current(false);
  });
}
