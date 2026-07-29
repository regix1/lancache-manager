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
  bgTertiary: '#182230',
  bgHover: '#4b5563',
  bgElevated: '#1c2a3a',
  bgSurface: '#313e52',
  bgSurfaceHover: '#3b4a63',
  bgSurfaceActive: '#46587a',
  bgOverlay: 'rgba(0,0,0,0.6)',

  // ── Text ─────────────────────────────────────────────────────────────
  textPrimary: '#ffffff',
  textSecondary: '#d1d5db',
  textMuted: '#9ca3af',
  // A lighter step of the primary on this palette, but not derived from it: accent text has to
  // move away from the page behind it, so a light theme needs a darker step where this one needs
  // a lighter one. The shipped light theme sets #1d4ed8 here. Each theme states its own. [18]
  textAccent: '#60a5fa',
  textPlaceholder: '#6b7280',

  // ── Drag handle ──────────────────────────────────────────────────────
  dragHandleColor: '#6b7280',
  // The shipped themes do not agree on this one: six lighten the accent for the hover and three
  // hover on the accent itself, so there is no step to compute and each theme states it. [18]
  dragHandleHover: '#60a5fa',

  // ── Borders ──────────────────────────────────────────────────────────
  borderPrimary: '#374151',
  borderSecondary: '#4b5563',
  borderElevated: '#3a4d63',
  borderHover: '#4a5f78',
  // Recessed data wells: a whisper edge so the fill change carries the boundary
  borderWell: 'rgba(255, 255, 255, 0.05)',

  // ── Navigation ───────────────────────────────────────────────────────
  navBg: '#1f2937',
  navBorder: '#374151',
  // navTabActive and navTabActiveBorder are left out on purpose: with no default here they
  // follow each theme's primaryColor instead of freezing on this palette's blue. [18]
  navTabInactive: '#9ca3af',
  navTabHover: '#ffffff',
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
  // info keeps a default even though it matches the primary: six of the eight shipped themes
  // deliberately give it a hue of their own, so it is a status colour, not the accent.
  info: '#3b82f6',
  // The fill and text of an info panel. They belong to info above rather than to the accent, and
  // they swap ends of the scale between palettes: this one wants a dark fill under light text,
  // the shipped light theme wants #dbeafe under #1d4ed8. Each theme states its own pair. [18]
  infoBg: '#1e3a8a',
  infoText: '#93c5fd',
  // Waiting/queued state (operation wait-queue cards). Purple on the default palette;
  // themes whose palettes clash with purple override with a distinct in-palette hue.
  waiting: '#a855f7',
  waitingBg: '#3b0764',
  waitingText: '#d8b4fe',

  // ── Service / platform colors ────────────────────────────────────────
  steamColor: '#10b981',
  epicColor: '#8b5cf6',
  originColor: '#fb923c',
  // No blizzardFaint / blizzardOnBorder / blizzardStrong here, and none for steam or epic:
  // normalizeThemeColors below fills those tints from each theme's own service color, but only
  // when the key is missing. A default here would win for every theme and freeze the tints on
  // this palette's blue, whatever blizzardColor the theme actually sets. [13]
  blizzardColor: '#3b82f6',
  wsusColor: '#06b6d4',
  riotColor: '#d13639',
  xboxColor: '#107C10',
  ubisoftColor: '#ec4899',
  gogColor: '#A05FB4',
  rockstarColor: '#FCAF17',
  arenanetColor: '#5C7A4A',
  bsgColor: '#6E7B3A',
  cityofheroesColor: '#5EC4E8',
  codColor: '#C2410C',
  daybreakColor: '#F2777A',
  frontierColor: '#D9A566',
  neverwinterColor: '#5B3A75',
  nexusmodsColor: '#F97316',
  nintendoColor: '#E4000F',
  pathofexileColor: '#B8860B',
  renegadexColor: '#6B7A8C',
  sonyColor: '#003791',
  squareColor: '#6B1210',
  tesoColor: '#5C1F35',
  testColor: '#71717A',
  warframeColor: '#1DD3D3',
  wargamingColor: '#5C5347',

  // ── Components ───────────────────────────────────────────────────────
  cardBg: '#1e2938',
  // One step up the same gray scale from cardBg (was #374151, gray-700 - only ~25/255 contrast
  // against the card background, making every card/modal border read as nearly invisible,
  // especially along a straight edge with nothing else nearby for the eye to anchor on).
  cardBorder: '#4b5563',
  // buttonBg and buttonHover are both left out on purpose: buttonBg follows primaryColor and
  // buttonHover is darken(buttonBg) below, so recolouring a theme's accent moves the pair
  // together. [18]
  buttonText: '#ffffff',
  inputBg: '#374151',
  inputBorder: '#4b5563',

  // ── Checkboxes ───────────────────────────────────────────────────────
  // checkboxAccent is left out on purpose so it follows primaryColor. [18]
  checkboxBorder: '#4b5563',
  checkboxBg: '#1f2937',
  checkboxCheckmark: '#ffffff',
  checkboxShadow: 'none',
  // checkboxHoverShadow is left out on purpose: the ring below builds it from checkboxAccent,
  // so only its 3px geometry is fixed and the colour follows the theme. [18]
  checkboxHoverBg: '#374151',

  // ── Sliders ──────────────────────────────────────────────────────────
  // sliderAccent and sliderThumb are left out on purpose so they follow primaryColor. [18]
  sliderTrack: '#374151',

  // ── Progress ─────────────────────────────────────────────────────────
  progressBg: '#374151',

  // ── Hit rate ─────────────────────────────────────────────────────────
  hitRateHighBg: '#064e3b',
  hitRateHighText: '#34d399',
  // The middle band of the hit-rate scale. Blue only because this palette's accent is blue: five
  // of the shipped themes make the middle band amber, between the green high band and the low
  // one, which is the scale doing its job rather than following the accent. [18]
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
  // iconBgBlue keeps a default even though it matches the primary: half the shipped themes give
  // it a hue of their own, and the name promises a blue rather than whatever the accent is.
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
  // chartColor1 is left out on purpose so it follows primaryColor: every shipped theme sets the
  // first series to its own accent. [18]
  chartColor2: '#10b981',
  chartColor3: '#f59e0b',
  chartColor4: '#ef4444',
  chartColor5: '#8b5cf6',
  chartColor6: '#06b6d4',
  chartColor7: '#f97316',
  chartColor8: '#ec4899',

  // ── Game chart colors (20 slots for Games on Disk doughnut) ─────────
  // gameColor1 keeps a default, unlike chartColor1: three of the shipped themes pick a first
  // slice that is not their accent, so this ramp is chosen for slice contrast on its own.
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
  scrollbarTrack: 'rgba(255, 255, 255, 0.06)',
  scrollbarThumb: 'rgba(255, 255, 255, 0.18)',
  scrollbarHover: 'rgba(255, 255, 255, 0.32)',

  // ── Access control indicators ────────────────────────────────────────
  publicAccessBg: 'rgba(16, 185, 129, 0.2)',
  publicAccessText: '#34d399',
  publicAccessBorder: 'rgba(16, 185, 129, 0.3)',
  securedAccessBg: 'rgba(245, 158, 11, 0.2)',
  securedAccessText: '#fbbf24',
  securedAccessBorder: 'rgba(245, 158, 11, 0.3)',

  // ── Sessions ─────────────────────────────────────────────────────────
  // userSessionColor and userSessionBg are left out on purpose: the signed-in marker is the
  // accent, and its fill is that same colour at 15%. [18]
  guestSessionColor: '#06b6d4',
  guestSessionBg: 'rgba(6, 182, 212, 0.15)',
  activeSessionColor: '#f97316',
  activeSessionBg: 'rgba(249, 115, 22, 0.15)',

  // ── Events ───────────────────────────────────────────────────────────
  // eventColor1 is left out on purpose so it follows primaryColor, like chartColor1. [18]
  eventColor2: '#10b981',
  eventColor3: '#f59e0b',
  eventColor4: '#ef4444',
  eventColor5: '#8b5cf6',
  eventColor6: '#ec4899',
  eventColor7: '#06b6d4',
  eventColor8: '#f97316',

  // ── Fireworks / celebration ──────────────────────────────────────────
  // fireworkColor1 and fireworkRocketColor are left out on purpose so they follow
  // primaryColor. [18]
  // fireworkColor2 is the second colour in the burst, not a shade of the first: four of the
  // shipped themes deliberately put a contrasting hue here so the two shells read apart.
  fireworkColor2: '#60a5fa',
  fireworkColor3: '#06b6d4',
  fireworkColor4: '#8b5cf6',
  fireworkColor5: '#22d3ee',
  fireworkColor6: '#a78bfa',
  fireworkColor7: '#38bdf8',
  fireworkColor8: '#ffffff',
  // The halo around the rocket. A lighter step of the primary here, but these palettes rotate hue
  // as they lighten, so a step that keeps the hue lands visibly off the mark and two themes give
  // the halo a hue of its own anyway. Each theme states it. [18]
  fireworkGlowColor: '#60a5fa'
};

// ---------------------------------------------------------------------------
// Read the red/green/blue channels out of a theme colour, or null when the value
// is not a colour this file can take apart. Handles #rrggbb, the #rgb shorthand,
// and the rgb()/rgba() forms the colour editor writes whenever a swatch is given
// an alpha. The alpha is read past and discarded; every caller here decides the
// opacity of what it is building.
// ---------------------------------------------------------------------------
function readColorChannels(color: string): [number, number, number] | null {
  const value = color.trim();

  const sixDigit = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(value);
  if (sixDigit) {
    return [parseInt(sixDigit[1], 16), parseInt(sixDigit[2], 16), parseInt(sixDigit[3], 16)];
  }

  const shorthand = /^#([a-f\d])([a-f\d])([a-f\d])$/i.exec(value);
  if (shorthand) {
    return [
      parseInt(shorthand[1] + shorthand[1], 16),
      parseInt(shorthand[2] + shorthand[2], 16),
      parseInt(shorthand[3] + shorthand[3], 16)
    ];
  }

  const rgbForm = /^rgba?\(\s*(\d+)[\s,]+(\d+)[\s,]+(\d+)(?:\s*[,/]\s*[\d.]+%?)?\s*\)$/i;
  const channels = rgbForm.exec(value);
  if (channels) {
    return [parseInt(channels[1], 10), parseInt(channels[2], 10), parseInt(channels[3], 10)];
  }

  return null;
}

// ---------------------------------------------------------------------------
// Utility: convert a base colour to an rgba string at the requested opacity.
// The base colour's own alpha is dropped: the caller asks for a specific opacity
// because the tint's job is to sit at that strength, whatever the colour it came
// from was set to.
// Anything unreadable falls back to mid grey rather than black, because black at a
// low opacity is invisible on a dark theme and the miscoloured tint would ship
// unnoticed.
// ---------------------------------------------------------------------------
export function hexToRgba(hex: string, opacity: number): string {
  const channels = readColorChannels(hex);
  if (!channels) {
    return `rgba(128, 128, 128, ${opacity})`;
  }

  return `rgba(${channels[0]}, ${channels[1]}, ${channels[2]}, ${opacity})`;
}

// ---------------------------------------------------------------------------
// Utility: the same colour, darker, as a #rrggbb string. Scales HSL lightness by
// the given factor and leaves hue and saturation where they were.
//
// Scaling lightness is what matches how the shipped themes were actually drawn:
// all nine write a hover that sits a step below their own button colour at
// essentially the same hue, and a 0.84 factor lands within a shade nobody can
// pick out of a line-up on both the dark and the light palette. Working in a
// perceptual space instead was measured and came out worse here, because it
// holds chroma flat while these ramps gain a little chroma as they darken.
//
// There is deliberately no lighter counterpart. A lighter step cannot be computed
// the same way: the shipped palettes rotate hue as they lighten, and a light theme
// wants its accent text darker rather than lighter, so one rule cannot serve both.
// Unreadable input gives mid grey, matching hexToRgba above. [18]
// ---------------------------------------------------------------------------
function darken(color: string, factor: number): string {
  const channels = readColorChannels(color);
  if (!channels) {
    return '#808080';
  }

  const [red, green, blue] = channels.map((channel) => channel / 255);
  const high = Math.max(red, green, blue);
  const low = Math.min(red, green, blue);
  const span = high - low;
  const lightness = ((high + low) / 2) * factor;

  const toChannel = (value: number): string =>
    Math.round(Math.max(0, Math.min(1, value)) * 255)
      .toString(16)
      .padStart(2, '0');

  if (span === 0) {
    const grey = toChannel(lightness);
    return `#${grey}${grey}${grey}`;
  }

  const saturation = (high + low) / 2 > 0.5 ? span / (2 - high - low) : span / (high + low);
  let hue: number;
  if (high === red) {
    hue = (green - blue) / span + (green < blue ? 6 : 0);
  } else if (high === green) {
    hue = (blue - red) / span + 2;
  } else {
    hue = (red - green) / span + 4;
  }
  hue /= 6;

  const upper =
    lightness < 0.5
      ? lightness * (1 + saturation)
      : lightness + saturation - lightness * saturation;
  const lower = 2 * lightness - upper;
  const at = (offset: number): number => {
    let position = offset;
    if (position < 0) position += 1;
    if (position > 1) position -= 1;
    if (position < 1 / 6) return lower + (upper - lower) * 6 * position;
    if (position < 1 / 2) return upper;
    if (position < 2 / 3) return lower + (upper - lower) * (2 / 3 - position) * 6;
    return lower;
  };

  return `#${toChannel(at(hue + 1 / 3))}${toChannel(at(hue))}${toChannel(at(hue - 1 / 3))}`;
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

  // Controls and markers that are the accent itself rather than a colour of their own.
  // They resolve here instead of carrying a default so that a theme setting primaryColor
  // gets one accent everywhere, without having to restate it a dozen times.
  if (!result.navTabActive) result.navTabActive = result.primaryColor;
  if (!result.navTabActiveBorder) result.navTabActiveBorder = result.primaryColor;
  if (!result.buttonBg) result.buttonBg = result.primaryColor;
  if (!result.checkboxAccent) result.checkboxAccent = result.primaryColor;
  if (!result.sliderAccent) result.sliderAccent = result.primaryColor;
  if (!result.sliderThumb) result.sliderThumb = result.primaryColor;
  if (!result.chartColor1) result.chartColor1 = result.primaryColor;
  if (!result.eventColor1) result.eventColor1 = result.primaryColor;
  if (!result.fireworkColor1) result.fireworkColor1 = result.primaryColor;
  if (!result.fireworkRocketColor) result.fireworkRocketColor = result.primaryColor;
  if (!result.userSessionColor) result.userSessionColor = result.primaryColor;

  // The signed-in marker's fill is that same colour at 15%, derived from userSessionColor
  // rather than the primary so an overridden marker keeps its fill in step
  if (!result.userSessionBg) result.userSessionBg = hexToRgba(result.userSessionColor, 0.15);

  // A button's hover is a step down from the button itself, so it comes off buttonBg rather
  // than the primary and stays in step with a theme that recolours only the button
  if (!result.buttonHover) result.buttonHover = darken(result.buttonBg, 0.84);

  // The checkbox hover ring: a fixed 3px of the checkbox's own accent at 10%. Only the
  // geometry is fixed, so the colour still follows the theme
  if (!result.checkboxHoverShadow) {
    result.checkboxHoverShadow = `0 0 0 3px ${hexToRgba(result.checkboxAccent, 0.1)}`;
  }

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

  // Battle.net (Blizzard) variants derived from blizzardColor
  const blizzard = result.blizzardColor;
  if (!result.blizzardFaint) result.blizzardFaint = hexToRgba(blizzard, 0.1);
  if (!result.blizzardOnBorder) result.blizzardOnBorder = hexToRgba(blizzard, 0.5);
  if (!result.blizzardStrong) result.blizzardStrong = hexToRgba(blizzard, 0.3);

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
