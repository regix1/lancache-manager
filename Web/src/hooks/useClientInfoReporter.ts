import { useEffect, useRef } from 'react';
import ApiService from '@services/api.service';

/**
 * Fetches the browser's public IP from api.ipify.org (free, unlimited, IPv4+IPv6)
 * and POSTs it together with navigator-derived locale / screen fields to the
 * backend so the session row can display country / city / ISP.
 *
 * Runs once per session (per cookie), and is a no-op when:
 *  - the caller is not authenticated
 *  - ipify is unreachable (corp firewall etc.) — the backend still accepts a
 *    payload without publicIp so timezone/language/screen get recorded anyway.
 *
 * Country-level GeoIP accuracy via ip-api.com (used server-side) is ~86%
 * based on ipapi.is's 2024 study; this is adequate for a "where from" badge.
 */

const IPIFY_URL = 'https://api.ipify.org?format=json';
const IPIFY_TIMEOUT_MS = 5_000;

const fetchPublicIp = async (): Promise<string | null> => {
  try {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), IPIFY_TIMEOUT_MS);
    const response = await fetch(IPIFY_URL, {
      signal: controller.signal,
      cache: 'no-store',
      credentials: 'omit'
    });
    clearTimeout(timeout);
    if (!response.ok) return null;
    const data = (await response.json()) as { ip?: string };
    return typeof data.ip === 'string' && data.ip.length > 0 ? data.ip : null;
  } catch {
    return null;
  }
};

const collectClientInfo = (): {
  timezone: string | null;
  language: string | null;
  screenResolution: string | null;
} => {
  let timezone: string | null = null;
  try {
    timezone = Intl.DateTimeFormat().resolvedOptions().timeZone ?? null;
  } catch {
    timezone = null;
  }

  const language =
    typeof navigator !== 'undefined' &&
    typeof navigator.language === 'string' &&
    navigator.language.length > 0
      ? navigator.language
      : null;

  let screenResolution: string | null = null;
  if (typeof window !== 'undefined' && window.screen) {
    const w = Math.round(window.screen.width);
    const h = Math.round(window.screen.height);
    if (w > 0 && h > 0) {
      screenResolution = `${w}x${h}`;
    }
  }

  return { timezone, language, screenResolution };
};

export const useClientInfoReporter = (enabled: boolean, sessionId: string | null): void => {
  const reportedSessionRef = useRef<string | null>(null);

  useEffect(() => {
    if (!enabled || !sessionId) return;
    if (reportedSessionRef.current === sessionId) return;
    reportedSessionRef.current = sessionId;

    let cancelled = false;
    const run = async () => {
      const [publicIp, info] = await Promise.all([
        fetchPublicIp(),
        Promise.resolve(collectClientInfo())
      ]);
      if (cancelled) return;
      try {
        await ApiService.updateOwnClientInfo({
          publicIp,
          timezone: info.timezone,
          language: info.language,
          screenResolution: info.screenResolution
        });
      } catch {
        // Best-effort — never block the UI on this.
      }
    };
    void run();

    return () => {
      cancelled = true;
    };
  }, [enabled, sessionId]);
};
