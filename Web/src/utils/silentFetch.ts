// utils/silentFetch.ts

interface FetchResult<T = any> {
  ok: boolean;
  data?: T;
  error?: string;
}

/**
 * Fetch data silently without throwing errors
 * Used for checking operation states without interrupting user flow
 */
export async function fetchStateOrNull<T = any>(url: string): Promise<FetchResult<T>> {
  try {
    const response = await fetch(url, {
      signal: AbortSignal.timeout(5000)
    });

    if (response.ok) {
      const data = await response.json();
      return { ok: true, data };
    }

    return { ok: false, error: `HTTP ${response.status}` };
  } catch (error: any) {
    // Silently handle errors - don't log to console to avoid noise
    return { ok: false, error: error.message };
  }
}

/**
 * Check if a resource exists without throwing errors
 */
export async function checkResourceExists(url: string): Promise<boolean> {
  try {
    const response = await fetch(url, {
      method: 'HEAD',
      signal: AbortSignal.timeout(3000)
    });
    return response.ok;
  } catch {
    return false;
  }
}

/**
 * Fetch with retry logic for resilient API calls
 */
export async function fetchWithRetry<T = any>(
  url: string,
  options: RequestInit = {},
  maxRetries = 3,
  retryDelay = 1000
): Promise<T> {
  let lastError: Error | null = null;

  for (let i = 0; i < maxRetries; i++) {
    try {
      const response = await fetch(url, {
        ...options,
        signal: options.signal || AbortSignal.timeout(10000)
      });

      if (response.ok) {
        return await response.json();
      }

      // Don't retry on client errors (4xx)
      if (response.status >= 400 && response.status < 500) {
        throw new Error(`HTTP ${response.status}: ${response.statusText}`);
      }

      lastError = new Error(`HTTP ${response.status}: ${response.statusText}`);
    } catch (error: any) {
      lastError = error;

      // Don't retry on abort
      if (error.name === 'AbortError') {
        throw error;
      }
    }

    // Wait before retrying (except on last attempt)
    if (i < maxRetries - 1) {
      await new Promise((resolve) => setTimeout(resolve, retryDelay * (i + 1)));
    }
  }

  throw lastError || new Error('Failed to fetch after retries');
}
