import { useEffect, useRef } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from './useReconnectRefetch';

interface SignInEndingWait {
  attemptId: string;
  deadline: number;
}

/**
 * Waits for the ending of a sign-in whose answer this page lost, without asking the server on a timer: `read` runs when
 * the server announces that attempt's ending, once right after it starts listening, when the hub connection comes back,
 * and once at the attempt's own deadline
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

  const attemptId = wait?.attemptId;
  useEffect(() => {
    if (!attemptId) return;
    const handleEnded = ({ attemptId: ended }: { attemptId: string }) => {
      if (ended === attemptId) readRef.current(false);
    };
    on('IntegrationLoginEnded', handleEnded);
    // An ending pushed before this subscription existed is read here, once.
    readRef.current(false);
    return () => off('IntegrationLoginEnded', handleEnded);
  }, [attemptId, on, off]);

  const deadline = wait?.deadline;
  useEffect(() => {
    if (deadline === undefined) return;
    const expire = setTimeout(() => readRef.current(true), Math.max(0, deadline - Date.now()));
    return () => clearTimeout(expire);
  }, [deadline]);

  useReconnectRefetch(isConnected, () => {
    if (waitRef.current) readRef.current(false);
  });
}
