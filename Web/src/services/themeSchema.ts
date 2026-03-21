import { z } from 'zod';

// ---------------------------------------------------------------------------
// Zod schema for ALL theme color properties.
// Defaults are the dark-default built-in theme values.
// This is the single source of truth for colour keys and their defaults.
// ---------------------------------------------------------------------------

const themeColorsSchema = z.object({
  // ── Core colors ──────────────────────────────────────────────────────
  primaryColor: z.string().default('#3b82f6'),
  secondaryColor: z.string().default('#8b5cf6'),
  accentColor: z.string().default('#06b6d4'),

  // ── Backgrounds ──────────────────────────────────────────────────────
  bgPrimary: z.string().default('#111827'),
  bgSecondary: z.string().default('#283649'),
  bgTertiary: z.string().default('#313e52'),
  bgHover: z.string().default('#4b5563'),
  bgElevated: z.string().default('#1c2a3a'),
  bgSurface: z.string().default('#223044'),
  bgSurfaceHover: z.string().default('#2a3a4e'),
  bgSurfaceActive: z.string().default('#324458'),
  bgOverlay: z.string().default('rgba(0,0,0,0.6)'),

  // ── Text ─────────────────────────────────────────────────────────────
  textPrimary: z.string().default('#ffffff'),
  textSecondary: z.string().default('#d1d5db'),
  textMuted: z.string().default('#9ca3af'),
  textAccent: z.string().default('#60a5fa'),
  textPlaceholder: z.string().default('#6b7280'),

  // ── Drag handle ──────────────────────────────────────────────────────
  dragHandleColor: z.string().default('#6b7280'),
  dragHandleHover: z.string().default('#60a5fa'),

  // ── Borders ──────────────────────────────────────────────────────────
  borderPrimary: z.string().default('#374151'),
  borderSecondary: z.string().default('#4b5563'),
  borderFocus: z.string().optional(),
  borderElevated: z.string().default('#3a4d63'),
  borderHover: z.string().default('#4a5f78'),

  // ── Navigation ───────────────────────────────────────────────────────
  navBg: z.string().default('#1f2937'),
  navBorder: z.string().default('#374151'),
  navTabActive: z.string().default('#3b82f6'),
  navTabInactive: z.string().default('#9ca3af'),
  navTabHover: z.string().default('#ffffff'),
  navTabActiveBorder: z.string().default('#3b82f6'),
  navMobileMenuBg: z.string().default('#1f2937'),
  navMobileItemHover: z.string().default('#374151'),

  // ── Status colors ────────────────────────────────────────────────────
  success: z.string().default('#10b981'),
  successBg: z.string().default('#064e3b'),
  successText: z.string().default('#34d399'),
  warning: z.string().default('#fb923c'),
  warningBg: z.string().default('#44403c'),
  warningText: z.string().default('#fcd34d'),
  error: z.string().default('#ef4444'),
  errorBg: z.string().default('#7f1d1d'),
  errorText: z.string().default('#fca5a5'),
  info: z.string().default('#3b82f6'),
  infoBg: z.string().default('#1e3a8a'),
  infoText: z.string().default('#93c5fd'),

  // ── Service / platform colors ────────────────────────────────────────
  steamColor: z.string().default('#10b981'),
  epicColor: z.string().default('#8b5cf6'),
  originColor: z.string().default('#fb923c'),
  blizzardColor: z.string().default('#3b82f6'),
  wsusColor: z.string().default('#06b6d4'),
  riotColor: z.string().default('#ef4444'),
  xboxColor: z.string().default('#107C10'),
  ubisoftColor: z.string().optional(),

  // ── Steam variants ───────────────────────────────────────────────────
  steamFaint: z.string().optional(),
  steamOnBorder: z.string().optional(),
  steamStrong: z.string().optional(),

  // ── Epic variants ────────────────────────────────────────────────────
  epicFaint: z.string().optional(),
  epicOnBorder: z.string().optional(),
  epicStrong: z.string().optional(),

  // ── Components ───────────────────────────────────────────────────────
  cardBg: z.string().default('#1e2938'),
  cardBorder: z.string().default('#374151'),
  cardOutline: z.string().optional(),
  buttonBg: z.string().default('#3b82f6'),
  buttonHover: z.string().default('#2563eb'),
  buttonText: z.string().default('#ffffff'),
  inputBg: z.string().default('#374151'),
  inputBorder: z.string().default('#4b5563'),
  inputFocus: z.string().optional(),

  // ── Checkboxes ───────────────────────────────────────────────────────
  checkboxAccent: z.string().default('#3b82f6'),
  checkboxBorder: z.string().default('#4b5563'),
  checkboxBg: z.string().default('#1f2937'),
  checkboxCheckmark: z.string().default('#ffffff'),
  checkboxShadow: z.string().default('none'),
  checkboxHoverShadow: z.string().default('0 0 0 3px rgba(59, 130, 246, 0.1)'),
  checkboxHoverBg: z.string().default('#374151'),
  checkboxFocus: z.string().optional(),

  // ── Sliders ──────────────────────────────────────────────────────────
  sliderAccent: z.string().default('#3b82f6'),
  sliderThumb: z.string().default('#3b82f6'),
  sliderTrack: z.string().default('#374151'),

  // ── Progress ─────────────────────────────────────────────────────────
  progressBg: z.string().default('#374151'),

  // ── Floating icon ────────────────────────────────────────────────────
  floatingIconColor: z.string().optional(),

  // ── Hit rate ─────────────────────────────────────────────────────────
  hitRateHighBg: z.string().default('#064e3b'),
  hitRateHighText: z.string().default('#34d399'),
  hitRateMediumBg: z.string().default('#1e3a8a'),
  hitRateMediumText: z.string().default('#93c5fd'),
  hitRateLowBg: z.string().default('#44403c'),
  hitRateLowText: z.string().default('#fbbf24'),
  hitRateWarningBg: z.string().default('#44403c'),
  hitRateWarningText: z.string().default('#fcd34d'),

  // ── Action buttons ───────────────────────────────────────────────────
  actionResetBg: z.string().default('#f59e0b'),
  actionResetHover: z.string().default('#d97706'),
  actionProcessBg: z.string().default('#10b981'),
  actionProcessHover: z.string().default('#059669'),
  actionDeleteBg: z.string().default('#ef4444'),
  actionDeleteHover: z.string().default('#dc2626'),

  // ── Icon backgrounds ─────────────────────────────────────────────────
  iconBgBlue: z.string().default('#3b82f6'),
  iconBgGreen: z.string().default('#10b981'),
  iconBgEmerald: z.string().default('#10b981'),
  iconBgPurple: z.string().default('#8b5cf6'),
  iconBgIndigo: z.string().default('#6366f1'),
  iconBgOrange: z.string().default('#f97316'),
  iconBgYellow: z.string().default('#eab308'),
  iconBgCyan: z.string().default('#06b6d4'),
  iconBgRed: z.string().default('#ef4444'),

  // ── Chart colors ─────────────────────────────────────────────────────
  chartColor1: z.string().default('#3b82f6'),
  chartColor2: z.string().default('#10b981'),
  chartColor3: z.string().default('#f59e0b'),
  chartColor4: z.string().default('#ef4444'),
  chartColor5: z.string().default('#8b5cf6'),
  chartColor6: z.string().default('#06b6d4'),
  chartColor7: z.string().default('#f97316'),
  chartColor8: z.string().default('#ec4899'),
  chartBorderColor: z.string().default('#1f2937'),
  chartGridColor: z.string().default('#374151'),
  chartTextColor: z.string().default('#9ca3af'),
  chartCacheHitColor: z.string().default('#10b981'),
  chartCacheMissColor: z.string().default('#f59e0b'),

  // ── Scrollbar ────────────────────────────────────────────────────────
  scrollbarTrack: z.string().default('#374151'),
  scrollbarThumb: z.string().default('#6B7280'),
  scrollbarHover: z.string().default('#9CA3AF'),

  // ── Access control indicators ────────────────────────────────────────
  publicAccessBg: z.string().default('rgba(16, 185, 129, 0.2)'),
  publicAccessText: z.string().default('#34d399'),
  publicAccessBorder: z.string().default('rgba(16, 185, 129, 0.3)'),
  securedAccessBg: z.string().default('rgba(245, 158, 11, 0.2)'),
  securedAccessText: z.string().default('#fbbf24'),
  securedAccessBorder: z.string().default('rgba(245, 158, 11, 0.3)'),

  // ── Sessions ─────────────────────────────────────────────────────────
  userSessionColor: z.string().default('#3b82f6'),
  userSessionBg: z.string().default('rgba(59, 130, 246, 0.15)'),
  guestSessionColor: z.string().default('#06b6d4'),
  guestSessionBg: z.string().default('rgba(6, 182, 212, 0.15)'),
  activeSessionColor: z.string().default('#f97316'),
  activeSessionBg: z.string().default('rgba(249, 115, 22, 0.15)'),

  // ── Events ───────────────────────────────────────────────────────────
  eventColor1: z.string().default('#3b82f6'),
  eventColor2: z.string().default('#10b981'),
  eventColor3: z.string().default('#f59e0b'),
  eventColor4: z.string().default('#ef4444'),
  eventColor5: z.string().default('#8b5cf6'),
  eventColor6: z.string().default('#ec4899'),
  eventColor7: z.string().default('#06b6d4'),
  eventColor8: z.string().default('#f97316'),

  // ── Fireworks / celebration ──────────────────────────────────────────
  fireworkColor1: z.string().default('#3b82f6'),
  fireworkColor2: z.string().default('#60a5fa'),
  fireworkColor3: z.string().default('#06b6d4'),
  fireworkColor4: z.string().default('#8b5cf6'),
  fireworkColor5: z.string().default('#22d3ee'),
  fireworkColor6: z.string().default('#a78bfa'),
  fireworkColor7: z.string().default('#38bdf8'),
  fireworkColor8: z.string().default('#ffffff'),
  fireworkRocketColor: z.string().default('#3b82f6'),
  fireworkGlowColor: z.string().default('#60a5fa')
});

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
// Parse a partial colour map, fill defaults via Zod, then normalise.
// ---------------------------------------------------------------------------
export function parseThemeColors(partial: Record<string, unknown>): Record<string, string> {
  const parsed = themeColorsSchema.parse(partial);
  // Filter out undefined values (optional derived keys) before normalizing
  const filtered: Record<string, string> = {};
  for (const [key, value] of Object.entries(parsed)) {
    if (value !== undefined) {
      filtered[key] = value;
    }
  }
  return normalizeThemeColors(filtered);
}
