import { useCallback, useEffect, useRef, useState } from 'react';
import { getErrorMessage, isAbortError } from '@utils/error';

/**
 * How long a loaded result is treated as current for a caller that only asks to be up to date.
 * A change already known to have happened takes the forced path, so it never waits on this window.
 */
const STALE_AFTER_MS = 30_000;

/** A request that never settles would hold the in-flight slot and leave the spinner up. */
const LOAD_TIMEOUT_MS = 30_000;

interface LoadLifecycleOptions<T> {
  /** Runs the request. The signal aborts on supersession, on the timeout and on unmount. */
  request: (signal: AbortSignal) => Promise<T>;
  /** Whether a load may run at all right now: a signed-out viewer has nothing to fetch. */
  canLoad: () => boolean;
  /** Publishes a response that is still the newest one. A superseded load never reaches it. */
  onLoaded: (value: T) => void;
  /**
   * A load that failed or timed out. `owned` is false when a newer load has already taken the
   * slot, so the caller may log the failure but must not write it over the newer load's state.
   */
  onFailed: (error: unknown, owned: boolean) => void;
  /** The owning load is about to send its request. */
  onStarted?: () => void;
  /** The owning load has settled; `loaded` is false when it failed or timed out. */
  onSettled?: (loaded: boolean) => void;
}

interface LoadLifecycle {
  /** `force` means the result is known to have changed, so freshness and coalescing are bypassed. */
  load: (force: boolean) => Promise<void>;
  /** The failure of the newest owning load, cleared when a later load succeeds. */
  error: string | null;
  /** Drops anything in flight and the freshness stamp, so the next load always fetches. */
  reset: () => void;
}

/**
 * The load lifecycle shared by every surface that fetches one list and keeps it current: one
 * in-flight request at a time, a forced load supersedes a running one, a caller that only asks to
 * be up to date is answered from a recent load, and a request that never settles is cut off.
 *
 * `load` and `reset` keep one identity for the lifetime of the component, so a caller may depend on
 * them without its effect re-running for this hook's own reasons.
 */
export function useLoadLifecycle<T>(options: LoadLifecycleOptions<T>): LoadLifecycle {
  const [error, setError] = useState<string | null>(null);
  const mountedRef = useRef(true);
  // Doubles as the in-flight flag and as the token deciding which response may publish.
  const abortControllerRef = useRef<AbortController | null>(null);
  // Settles when the owning load ends, so a caller folded into it can wait for that load.
  const runningLoadRef = useRef<Promise<void> | null>(null);
  const loadedAtRef = useRef(0);
  // Held in a ref so a new callback identity each render never re-creates `load`.
  const optionsRef = useRef(options);

  useEffect(() => {
    optionsRef.current = options;
  }, [options]);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      abortControllerRef.current?.abort();
      abortControllerRef.current = null;
    };
  }, []);

  const load = useCallback(async (force: boolean): Promise<void> => {
    // Waits for the running load and for each load that replaces it, so a caller such as Retry
    // stays busy until the newest load ends. It stops when the slot is empty or no longer moves,
    // which is what a reset or an unmount leaves.
    const waitForNewestLoad = async (): Promise<void> => {
      let awaited = runningLoadRef.current;
      while (awaited !== null) {
        await awaited;
        awaited = runningLoadRef.current === awaited ? null : runningLoadRef.current;
      }
    };
    if (!mountedRef.current) return;
    const settings = optionsRef.current;
    if (!settings.canLoad()) return;

    if (abortControllerRef.current) {
      // Restarting a load already fetching these rows would only delay them, and repeated
      // triggers could starve it. A forced load supersedes: that answer predates the change.
      // A caller that folds in waits for the running load, so a Retry stays busy until it ends.
      if (!force) {
        await waitForNewestLoad();
        return;
      }
      abortControllerRef.current.abort();
    } else if (!force && Date.now() - loadedAtRef.current < STALE_AFTER_MS) {
      return;
    }

    const controller = new AbortController();
    const loadTimeout = AbortSignal.timeout(LOAD_TIMEOUT_MS);
    // A timeout has to be told apart from a supersession: a timed-out load is the newest one and
    // must report a failure the viewer can retry, while a superseded one stays silent because its
    // replacement is already publishing. Carrying the timeout reason through keeps the rejection
    // out of the cancel path as well.
    let timedOut = false;
    const abortOnTimeout = (): void => {
      timedOut = true;
      controller.abort(loadTimeout.reason);
    };
    let loaded = false;
    let settle!: () => void;
    const running = new Promise<void>((resolve) => {
      settle = resolve;
    });
    try {
      // Claimed inside the try so the finally below always releases it.
      abortControllerRef.current = controller;
      runningLoadRef.current = running;
      loadTimeout.addEventListener('abort', abortOnTimeout, { once: true });
      // The last failure stays drawn until a result replaces it, so its Retry keeps the box.
      settings.onStarted?.();
      const value = await settings.request(controller.signal);
      // An abort after the response arrived cannot reject, so ownership is re-checked here.
      if (mountedRef.current && abortControllerRef.current === controller) {
        loadedAtRef.current = Date.now();
        settings.onLoaded(value);
        // A result that arrived heals whatever the last failure said, including one left behind
        // by a load this one superseded.
        setError(null);
        loaded = true;
      }
    } catch (err) {
      // Supersession, unmount and session loss all arrive as the same cancel and stay silent.
      if (isAbortError(err) && !timedOut) return;
      const owned = abortControllerRef.current === controller;
      if (owned) {
        // The previous rows stay on screen, so the freshness window may no longer vouch for
        // them; the next caller has to fetch again.
        loadedAtRef.current = 0;
        // Only the owner may report the failure: a load already replaced would otherwise put an
        // error over the rows its replacement is about to publish.
        if (mountedRef.current) setError(getErrorMessage(err));
      }
      settings.onFailed(err, owned);
    } finally {
      loadTimeout.removeEventListener('abort', abortOnTimeout);
      // Only the current owner reports completion, so a superseded load cannot clear the
      // spinner or release the slot its replacement holds.
      if (abortControllerRef.current === controller) {
        abortControllerRef.current = null;
        runningLoadRef.current = null;
        if (mountedRef.current) settings.onSettled?.(loaded);
      }
      settle();
      // A load that replaced this one decides what the caller sees, so the caller waits for it.
      await waitForNewestLoad();
    }
  }, []);

  const reset = useCallback((): void => {
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    runningLoadRef.current = null;
    // Loading again must fetch rather than trust the timestamp of discarded rows.
    loadedAtRef.current = 0;
    // The failure described the discarded rows, so it goes with them.
    setError(null);
  }, []);

  return { load, error, reset };
}
