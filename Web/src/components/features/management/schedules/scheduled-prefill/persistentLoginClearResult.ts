/**
 * Normalizes the response of POST .../persistent/clear-logins into a flat per-service list.
 * Matches the backend's `ClearPersistentLoginsResponseDto` (`Api/LancacheManager/Controllers/
 * PersistentPrefillController.cs`): `{ services: [{ service, wasRunning, success, detail }] }`.
 * Still validated defensively (the API layer types the response as `unknown`) rather than cast,
 * since this crosses the network boundary.
 */

import { isRecord } from './typeGuards';

type PersistentLoginClearOutcome = 'loggedOut' | 'volumeRemoved' | 'failed';

interface PersistentLoginClearResult {
  service: string;
  outcome: PersistentLoginClearOutcome;
  detail?: string;
}

function toResult(item: Record<string, unknown>): PersistentLoginClearResult | null {
  if (typeof item.service !== 'string' || typeof item.success !== 'boolean') {
    return null;
  }

  const wasRunning = item.wasRunning === true;
  const outcome: PersistentLoginClearOutcome = !item.success
    ? 'failed'
    : wasRunning
      ? 'loggedOut'
      : 'volumeRemoved';

  return {
    service: item.service,
    outcome,
    detail: typeof item.detail === 'string' ? item.detail : undefined
  };
}

export function normalizePersistentLoginClearResults(
  response: unknown
): PersistentLoginClearResult[] {
  if (!isRecord(response) || !Array.isArray(response.services)) {
    return [];
  }

  const results: PersistentLoginClearResult[] = [];
  for (const item of response.services) {
    if (!isRecord(item)) {
      continue;
    }
    const result = toResult(item);
    if (result) {
      results.push(result);
    }
  }

  return results;
}
