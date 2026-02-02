import { useRef, useCallback } from 'react';

/**
 * Information about an abortable request
 */
export interface AbortableRequest {
  /**
   * Unique identifier for this request
   */
  requestId: number;

  /**
   * AbortSignal to pass to fetch/API calls
   */
  signal: AbortSignal;

  /**
   * Function to abort this request
   */
  abort: () => void;
}

/**
 * Return value from the useAbortableRequest hook
 */
export interface AbortableRequestReturn {
  /**
   * Creates a new abortable request. Each call returns a new request ID and abort controller.
   * The request ID can be used with isCurrentRequest to prevent race conditions.
   *
   * @returns Object containing requestId, signal, and abort function
   */
  createRequest: () => AbortableRequest;

  /**
   * Checks if the given request ID is still the current (most recent) request.
   * Use this to prevent stale requests from updating state.
   *
   * @param id - Request ID to check
   * @returns true if this is the current request, false otherwise
   */
  isCurrentRequest: (id: number) => boolean;
}

/**
 * Hook that manages abort controllers and request IDs to prevent race conditions.
 * This is useful for data fetching where rapid filter changes could cause overlapping
 * requests, and you want to ensure only the most recent request can update state.
 *
 * Pattern:
 * 1. Call createRequest() at the start of your async operation
 * 2. Pass the signal to your fetch/API call
 * 3. Before updating state, check isCurrentRequest(requestId)
 * 4. If false, discard the result (a newer request has started)
 *
 * @example
 * ```tsx
 * const { createRequest, isCurrentRequest } = useAbortableRequest();
 *
 * const fetchData = async () => {
 *   const { requestId, signal, abort } = createRequest();
 *
 *   try {
 *     const data = await ApiService.getData(signal);
 *
 *     // Only update state if this is still the current request
 *     if (isCurrentRequest(requestId)) {
 *       setData(data);
 *     }
 *   } catch (err) {
 *     if (isCurrentRequest(requestId)) {
 *       setError(err.message);
 *     }
 *   }
 * };
 * ```
 */
export function useAbortableRequest(): AbortableRequestReturn {
  // Ref to track the current abort controller
  const abortControllerRef = useRef<AbortController | null>(null);

  // Counter for generating unique request IDs
  const currentRequestIdRef = useRef(0);

  /**
   * Creates a new abortable request, aborting any previous request.
   */
  const createRequest = useCallback((): AbortableRequest => {
    // Abort any in-flight request
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }

    // Generate unique request ID - only this request can modify state
    const requestId = ++currentRequestIdRef.current;

    // Create new abort controller
    const abortController = new AbortController();
    abortControllerRef.current = abortController;

    return {
      requestId,
      signal: abortController.signal,
      abort: () => abortController.abort()
    };
  }, []);

  /**
   * Checks if the given request ID is still the current request.
   */
  const isCurrentRequest = useCallback((id: number): boolean => {
    return currentRequestIdRef.current === id;
  }, []);

  return {
    createRequest,
    isCurrentRequest
  };
}
