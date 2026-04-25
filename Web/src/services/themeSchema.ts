// ---------------------------------------------------------------------------
// Theme color defaults (dark-default built-in theme values).
// This is the single source of truth for colour keys and their defaults.
// ---------------------------------------------------------------------------

const themeColorDefaults: Record<string, string> = {
  // ── Core colors ──────────────────────────────────────────────────────
  primaryColor: '#3b82f6',
  secondaryColor: '#8b5cf6',
  accentColor: '#06b6d4',

  // ── Backgrounds ──────────────────────────────────────────────────────
  bgPrimary: '#111827',
  bgSecondary: '#283649',
  bgTertiary: '#313e52',
  bgHover: '#4b5563',
  bgElevated: '#1c2a3a',
  bgSurface: '#223044',
  bgSurfaceHover: '#2a3a4e',
  bgSurfaceActive: '#324458',
  bgOverlay: 'rgba(0,0,0,0.6)',

  // ── Text ─────────────────────────────────────────────────────────────
  textPrimary: '#ffffff',
  textSecondary: '#d1d5db',
  textMuted: '#9ca3af',
  textAccent: '#60a5fa',
  textPlaceholder: '#6b7280',

  // ── Drag handle ──────────────────────────────────────────────────────
  dragHandleColor: '#6b7280',
  dragHandleHover: '#60a5fa',

  // ── Borders ──────────────────────────────────────────────────────────
  borderPrimary: '#374151',
  borderSecondary: '#4b5563',
  borderElevated: '#3a4d63',
  borderHover: '#4a5f78',

  // ── Navigation ───────────────────────────────────────────────────────
  navBg: '#1f2937',
  navBorder: '#374151',
  navTabActive: '#3b82f6',
  navTabInactive: '#9ca3af',
  navTabHover: '#ffffff',
  navTabActiveBorder: '#3b82f6',
  navMobileMenuBg: '#1f2937',
  navMobileItemHover: '#374151',

  // ── Status colors ────────────────────────────────────────────────────
  success: '#10b981',
  successBg: '#064e3b',
  successText: '#34d399',
  warning: '#fb923c',
  warningBg: '#44403c',
  warningText: '#fcd34d',
  error: '#ef4444',
  errorBg: '#7f1d1d',
  errorText: '#fca5a5',
  info: '#3b82f6',
  infoBg: '#1e3a8a',
  infoText: '#93c5fd',

  // ── Service / platform colors ────────────────────────────────────────
  steamColor: '#10b981',
  epicColor: '#8b5cf6',
  originColor: '#fb923c',
  blizzardColor: '#3b82f6',
  wsusColor: '#06b6d4',
  riotColor: '#ef4444',
  xboxColor: '#107C10',

  // ── Components ───────────────────────────────────────────────────────
  cardBg: '#1e2938',
  cardBorder: '#374151',
  buttonBg: '#3b82f6',
  buttonHover: '#2563eb',
  buttonText: '#ffffff',
  inputBg: '#374151',
  inputBorder: '#4b5563',

  // ── Checkboxes ───────────────────────────────────────────────────────
  checkboxAccent: '#3b82f6',
  checkboxBorder: '#4b5563',
  checkboxBg: '#1f2937',
  checkboxCheckmark: '#ffffff',
  checkboxShadow: 'none',
  checkboxHoverShadow: '0 0 0 3px rgba(59, 130, 246, 0.1)',
  checkboxHoverBg: '#374151',

  // ── Sliders ──────────────────────────────────────────────────────────
  sliderAccent: '#3b82f6',
  sliderThumb: '#3b82f6',
  sliderTrack: '#374151',

  // ── Progress ─────────────────────────────────────────────────────────
  progressBg: '#374151',

  // ── Hit rate ─────────────────────────────────────────────────────────
  hitRateHighBg: '#064e3b',
  hitRateHighText: '#34d399',
  hitRateMediumBg: '#1e3a8a',
  hitRateMediumText: '#93c5fd',
  hitRateLowBg: '#44403c',
  hitRateLowText: '#fbbf24',
  hitRateWarningBg: '#44403c',
  hitRateWarningText: '#fcd34d',

  // ── Action buttons ───────────────────────────────────────────────────
  actionResetBg: '#f59e0b',
  actionResetHover: '#d97706',
  actionProcessBg: '#10b981',
  actionProcessHover: '#059669',
  actionDeleteBg: '#ef4444',
  actionDeleteHover: '#dc2626',

  // ── Icon backgrounds ─────────────────────────────────────────────────
  iconBgBlue: '#3b82f6',
  iconBgGreen: '#10b981',
  iconBgEmerald: '#10b981',
  iconBgPurple: '#8b5cf6',
  iconBgIndigo: '#6366f1',
  iconBgOrange: '#f97316',
  iconBgYellow: '#eab308',
  iconBgCyan: '#06b6d4',
  iconBgTeal: '#14b8a6',
  iconBgRed: '#ef4444',

  // ── Chart colors ─────────────────────────────────────────────────────
  chartColor1: '#3b82f6',
  chartColor2: '#10b981',
  chartColor3: '#f59e0b',
  chartColor4: '#ef4444',
  chartColor5: '#8b5cf6',
  chartColor6: '#06b6d4',
  chartColor7: '#f97316',
  chartColor8: '#ec4899',

  // ── Game chart colors (20 slots for Games on Disk doughnut) ─────────
  gameColor1: '#3b82f6',
  gameColor2: '#10b981',
  gameColor3: '#f59e0b',
  gameColor4: '#ef4444',
  gameColor5: '#8b5cf6',
  gameColor6: '#06b6d4',
  gameColor7: '#f97316',
  gameColor8: '#ec4899',
  gameColor9: '#14b8a6',
  gameColor10: '#a855f7',
  gameColor11: '#eab308',
  gameColor12: '#6366f1',
  gameColor13: '#84cc16',
  gameColor14: '#e11d48',
  gameColor15: '#0ea5e9',
  gameColor16: '#d946ef',
  gameColor17: '#22c55e',
  gameColor18: '#f43f5e',
  gameColor19: '#0891b2',
  gameColor20: '#c084fc',
  gameColorOther: '#6b7280',

  chartBorderColor: '#1f2937',
  chartGridColor: '#374151',
  chartTextColor: '#9ca3af',
  chartCacheHitColor: '#10b981',
  chartCacheMissColor: '#f59e0b',

  // ── Scrollbar ────────────────────────────────────────────────────────
  scrollbarTrack: '#374151',
  scrollbarThumb: '#6B7280',
  scrollbarHover: '#9CA3AF',

  // ── Access control indicators ────────────────────────────────────────
  publicAccessBg: 'rgba(16, 185, 129, 0.2)',
  publicAccessText: '#34d399',
  publicAccessBorder: 'rgba(16, 185, 129, 0.3)',
  securedAccessBg: 'rgba(245, 158, 11, 0.2)',
  securedAccessText: '#fbbf24',
  securedAccessBorder: 'rgba(245, 158, 11, 0.3)',

  // ── Sessions ─────────────────────────────────────────────────────────
  userSessionColor: '#3b82f6',
  userSessionBg: 'rgba(59, 130, 246, 0.15)',
  guestSessionColor: '#06b6d4',
  guestSessionBg: 'rgba(6, 182, 212, 0.15)',
  activeSessionColor: '#f97316',
  activeSessionBg: 'rgba(249, 115, 22, 0.15)',

  // ── Events ───────────────────────────────────────────────────────────
  eventColor1: '#3b82f6',
  eventColor2: '#10b981',
  eventColor3: '#f59e0b',
  eventColor4: '#ef4444',
  eventColor5: '#8b5cf6',
  eventColor6: '#ec4899',
  eventColor7: '#06b6d4',
  eventColor8: '#f97316',

  // ── Fireworks / celebration ──────────────────────────────────────────
  fireworkColor1: '#3b82f6',
  fireworkColor2: '#60a5fa',
  fireworkColor3: '#06b6d4',
  fireworkColor4: '#8b5cf6',
  fireworkColor5: '#22d3ee',
  fireworkColor6: '#a78bfa',
  fireworkColor7: '#38bdf8',
  fireworkColor8: '#ffffff',
  fireworkRocketColor: '#3b82f6',
  fireworkGlowColor: '#60a5fa'
};

// ---------------------------------------------------------------------------
// Utility: convert hex colour to rgba string
// ---------------------------------------------------------------------------
export function hexToRgba(hex: string, opacity: number): string {
  const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
  if (!result) return `rgba(0, 0, 0, ${opacity})`;
  return `rgba(${parseInt(result[1], 16)}, ${parseInt(result[2], 16)}, ${parseInt(result[3], 16)}, ${opacity})`;
}

// ---------------------------------------------------------------------------
// Fill in derived colours that depend on other base colours.
// Only sets a key when it is missing or empty.
// ---------------------------------------------------------------------------
function normalizeThemeColors(colors: Record<string, string>): Record<string, string> {
  const result = { ...colors };

  // Focus colours default to primary
  if (!result.borderFocus) result.borderFocus = result.primaryColor;
  if (!result.inputFocus) result.inputFocus = result.primaryColor;
  if (!result.checkboxFocus) result.checkboxFocus = result.primaryColor;
  if (!result.cardOutline) result.cardOutline = result.primaryColor;
  if (!result.floatingIconColor) result.floatingIconColor = result.primaryColor;

  // Steam variants derived from steamColor
  const steam = result.steamColor;
  if (!result.steamFaint) result.steamFaint = hexToRgba(steam, 0.1);
  if (!result.steamOnBorder) result.steamOnBorder = hexToRgba(steam, 0.5);
  if (!result.steamStrong) result.steamStrong = hexToRgba(steam, 0.3);

  // Epic variants derived from epicColor
  const epic = result.epicColor;
  if (!result.epicFaint) result.epicFaint = hexToRgba(epic, 0.1);
  if (!result.epicOnBorder) result.epicOnBorder = hexToRgba(epic, 0.5);
  if (!result.epicStrong) result.epicStrong = hexToRgba(epic, 0.3);

  // Ubisoft defaults to epic
  if (!result.ubisoftColor) result.ubisoftColor = result.epicColor;

  return result;
}

// ---------------------------------------------------------------------------
// Parse a partial colour map, fill defaults, then normalise.
// ---------------------------------------------------------------------------
export function parseThemeColors(partial: Record<string, unknown>): Record<string, string> {
  const result: Record<string, string> = { ...themeColorDefaults };
  for (const [key, value] of Object.entries(partial)) {
    if (typeof value === 'string' && value !== '') {
      result[key] = value;
    }
  }
  return normalizeThemeColors(result);
}
