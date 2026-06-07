/* eslint-disable no-console -- intentional debug logging for banner scaling diagnostics */
const DEBUG_STORAGE_KEY = 'debug.bannerImage';

function isBannerImageDebugEnabled(): boolean {
  if (import.meta.env.DEV) return true;
  try {
    return localStorage.getItem(DEBUG_STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

export function logBannerImageDebug(
  scope: 'toggle' | 'game-image' | 'scaling' | 'backend',
  message: string,
  data?: Record<string, unknown>
): void {
  if (!isBannerImageDebugEnabled()) return;

  const payload = data ? { ...data } : undefined;
  console.log(`[BannerImage:${scope}] ${message}`, payload ?? '');
}

export function warnBannerImageDebug(
  scope: 'toggle' | 'game-image' | 'scaling' | 'backend',
  message: string,
  data?: Record<string, unknown>
): void {
  if (!isBannerImageDebugEnabled()) return;

  const payload = data ? { ...data } : undefined;
  console.warn(`[BannerImage:${scope}] ${message}`, payload ?? '');
}
