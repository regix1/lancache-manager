import { useEffect, useRef } from 'react';
import ApiService from '@services/api.service';

/**
 * POSTs navigator-derived locale / screen fields to the backend so the session row
 * can display country / city / ISP.
 *
 * Runs once per session (per cookie), and is a no-op when the caller is not
 * authenticated.
 *
 * The public IP is resolved entirely on the server. The browser used to fetch it from
 * api.ipify.org, but a lancache box normally sits behind pi-hole or another DNS filter,
 * so that request failed on most installs and logged a network error in the console on
 * every session. The server sees the caller's address on the connection already, and
 * falls back to its own lookup when that address is a LAN one - both of which work with
 * no third-party request from the user's browser.
 *
 * Country-level GeoIP accuracy via ip-api.com (used server-side) is ~86%
 * based on ipapi.is's 2024 study; this is adequate for a "where from" badge.
 */

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
      const info = collectClientInfo();
      if (cancelled) return;
      try {
        await ApiService.updateOwnClientInfo({
          publicIp: null,
          timezone: info.timezone,
          language: info.language,
          screenResolution: info.screenResolution
        });
      } catch {
        // Best-effort - never block the UI on this.
      }
    };
    void run();

    return () => {
      cancelled = true;
    };
  }, [enabled, sessionId]);
};
