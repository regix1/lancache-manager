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
