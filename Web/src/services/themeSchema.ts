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
  // textAccent is left out on purpose: it is one step of the accent toward the page's own text,
  // and normalizeThemeColors below reads which way that is off the palette.
  textPlaceholder: '#6b7280',

  // ── Drag handle ──────────────────────────────────────────────────────
  dragHandleColor: '#6b7280',
  // The shipped themes do not agree on this one: six lighten the accent for the hover and three
  // hover on the accent itself, so there is no step to compute and each theme states it.
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
  // follow each theme's primaryColor instead of freezing on this palette's blue.
  navTabInactive: '#9ca3af',
  navTabHover: '#ffffff',
  navMobileMenuBg: '#1f2937',
  navMobileItemHover: '#374151',

  // ── Status colors ────────────────────────────────────────────────────
  success: '#10b981',
  // A hair below the old #064e3b so the green above it clears 4.5:1 sitting on it: the status
  // glyph paints itself in `success` on this ground rather than in successText, and at the old
  // depth that pairing read 3.83:1.
  successBg: '#053f30',
  successText: '#34d399',
  warning: '#fb923c',
  warningBg: '#44403c',
  warningText: '#fcd34d',
  // Lifted off red-500, which reads 3.26:1 against this palette's card and 2.66:1 on its own badge
  // ground. This colour is the error INK far more often than it is an error fill - roughly 26 text
  // rules against one filled button - so it steps toward the page's text rather than away.
  error: '#f47d7d',
  // Deepened to match: the ink above needs a ground this far down to clear 4.5:1, and the badge
  // text that already sat here gains with it.
  errorBg: '#661616',
  errorText: '#fca5a5',
  // info keeps a default even though it matches the primary: six of the eight shipped themes
  // deliberately give it a hue of their own, so it is a status colour, not the accent.
  info: '#3b82f6',
  // infoBg and infoText are left out on purpose. They are shades of info above, not of the accent:
  // all nine shipped themes draw this pair from their own info, and both derive below off info so
  // the badge and the words on it cannot end up different hues.
  // Waiting/queued state (operation wait-queue cards). Purple on the default palette;
  // themes whose palettes clash with purple override with a distinct in-palette hue.
  waiting: '#a855f7',
  waitingBg: '#3b0764',
  waitingText: '#d8b4fe',

  // ── Service / platform colors ────────────────────────────────────────
  // Each service carries its own brand hue rather than borrowing one of the semantic colors.
  // Borrowing costs the most in the one card where it shows: Service Analytics puts the services
  // on one tab and cache hits on the next, so a service painted in the success green means
  // "Steam" on one tab and "cache hit" on the other, at the same size and in the same place.
  steamColor: '#66c0f4', // Valve's accent blue; Steam's palette holds no green
  epicColor: '#8b5cf6', // Epic's brand is black and white only, so this is a stand-in hue
  // A light EA orange rather than the wordmark red: two reds sat 15.2 dE apart (10.0 protan),
  // so Origin and Riot chips were near-twins. This value is 44.7 from riot (28.2 deutan) while
  // holding 28.3 from nexusmods and 19.8 from graphite's cod, the nearest oranges.
  originColor: '#ffa35c',
  // No blizzardFaint / blizzardOnBorder / blizzardStrong here, and none for steam or epic:
  // normalizeThemeColors below fills those tints from each theme's own service color, but only
  // when the key is missing. A default here would win for every theme and freeze the tints on
  // this palette's blue, whatever blizzardColor the theme actually sets.
  blizzardColor: '#5d6bdc', // Battle.net's royal blue turned toward indigo, clear of the accent
  // Off the accent, which it used to match exactly: a WSUS chip painted in the accent stops saying
  // WSUS. A paler ice cyan sits 20.3 dE from the accent and 16.7 from its nearest service neighbour,
  // where the old value was 0.0 and 10.5, and it reads 7.31:1 on the card instead of 5.05:1.
  wsusColor: '#8ad4dc',
  riotColor: '#d13639',
  xboxColor: '#107C10',
  ubisoftColor: '#c6bdff', // Ubisoft's blue pushed along the violet axis; their palette holds no pink
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
  // together.
  buttonText: '#ffffff',
  inputBg: '#374151',
  inputBorder: '#4b5563',

  // ── Checkboxes ───────────────────────────────────────────────────────
  // checkboxAccent is left out on purpose so it follows primaryColor.
  checkboxBorder: '#4b5563',
  checkboxBg: '#1f2937',
  checkboxCheckmark: '#ffffff',
  checkboxShadow: 'none',
  // checkboxHoverShadow is left out on purpose: the ring below builds it from checkboxAccent,
  // so only its 3px geometry is fixed and the colour follows the theme.
  checkboxHoverBg: '#374151',

  // ── Sliders ──────────────────────────────────────────────────────────
  // sliderAccent and sliderThumb are left out on purpose so they follow primaryColor.
  sliderTrack: '#374151',

  // ── Progress ─────────────────────────────────────────────────────────
  progressBg: '#374151',

  // ── Hit rate ─────────────────────────────────────────────────────────
  hitRateHighBg: '#064e3b',
  hitRateHighText: '#34d399',
  // The middle band of the hit-rate scale. Blue only because this palette's accent is blue: five
  // of the shipped themes make the middle band amber, between the green high band and the low
  // one, which is the scale doing its job rather than following the accent.
  hitRateMediumBg: '#1e3a8a',
  hitRateMediumText: '#93c5fd',
  hitRateLowBg: '#44403c',
  hitRateLowText: '#fbbf24',
  hitRateWarningBg: '#44403c',
  hitRateWarningText: '#fcd34d',

  // ── Action buttons ───────────────────────────────────────────────────
  // Every filled button in the app takes one shared label colour, buttonText below, so each fill
  // has to carry white on its own. The 500 steps these were cut from cannot: white reads 2.15:1 on
  // amber-500 and 2.54:1 on emerald-500, and no white-on-mid-tone fill can be fixed from the label
  // end while the label is shared. Amber and emerald are the two brightest hues here and take two
  // steps down the same ramp; red takes one. Each hover is the next step below its own rest state,
  // so the pair still darkens on hover the way the rest of the palette does.
  actionResetBg: '#b45309',
  actionResetHover: '#92400e',
  actionProcessBg: '#047857',
  actionProcessHover: '#065f46',
  // Red goes two steps rather than the one white needs, because reset and delete sit side by side
  // and a red-green deficiency reads both of them by lightness alone. At amber-700 against red-600
  // those lightnesses match, and the two buttons became the same colour under simulated
  // deuteranopia (CIE76 dE 3.1). Red-800 reopens that to 19.7 and holds 29.9 under protanopia.
  actionDeleteBg: '#991b1b',
  actionDeleteHover: '#7f1d1d',

  // ── Icon backgrounds ─────────────────────────────────────────────────
  // iconBgBlue keeps a default even though it matches the primary: half the shipped themes give
  // it a hue of their own, and the name promises a blue rather than whatever the accent is.
  // These feed the .icon-* text classes, so each one is read as a glyph against the card rather
  // than seen as a swatch. Blue, purple, indigo, orange and red were cut from the 500/600 steps and
  // land between 2.74:1 and 4.37:1 on this palette's card; each moves up its own ramp until it
  // clears 4.5:1 with room to spare. Green, emerald, yellow, cyan and teal already did and stay put.
  iconBgBlue: '#60a5fa',
  iconBgGreen: '#10b981',
  iconBgEmerald: '#10b981',
  iconBgPurple: '#b18cf7',
  iconBgIndigo: '#9ba5fa',
  iconBgOrange: '#fb923c',
  iconBgYellow: '#eab308',
  iconBgCyan: '#06b6d4',
  iconBgTeal: '#14b8a6',
  iconBgRed: '#f47d7d',

  // ── Chart colors ─────────────────────────────────────────────────────
  // chartColor1 is left out on purpose so it follows primaryColor: every shipped theme sets the
  // first series to its own accent.
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

  // Holds the same value as cardBg above: the doughnut's slice ring is meant to be the card
  // showing through, so any gap between the two draws a faint outline nothing asked for. Every
  // theme that moves cardBg restates this key to match.
  chartBorderColor: '#1e2938',
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
  // accent, and its fill is that same colour at 15%.
  guestSessionColor: '#06b6d4',
  guestSessionBg: 'rgba(6, 182, 212, 0.15)',
  activeSessionColor: '#f97316',
  activeSessionBg: 'rgba(249, 115, 22, 0.15)',

  // ── Events ───────────────────────────────────────────────────────────
  // eventColor1 is left out on purpose so it follows primaryColor, like chartColor1.
  eventColor2: '#10b981',
  eventColor3: '#f59e0b',
  eventColor4: '#ef4444',
  eventColor5: '#8b5cf6',
  eventColor6: '#ec4899',
  eventColor7: '#06b6d4',
  eventColor8: '#f97316',

  // ── Fireworks / celebration ──────────────────────────────────────────
  // fireworkColor1 and fireworkRocketColor are left out on purpose so they follow
  // primaryColor.
  // fireworkColor2 is the second colour in the burst, not a shade of the first: four of the
  // shipped themes deliberately put a contrasting hue here so the two shells read apart.
  fireworkColor2: '#60a5fa',
  fireworkColor3: '#06b6d4',
  fireworkColor4: '#8b5cf6',
  fireworkColor5: '#22d3ee',
  fireworkColor6: '#a78bfa',
  fireworkColor7: '#38bdf8',
  fireworkColor8: '#ffffff'
  // fireworkGlowColor is left out on purpose: the halo is a lighter shade of the rocket it
  // surrounds, on a dark page and a light one alike, so it derives below without flipping.
};

// ---------------------------------------------------------------------------
// Read the red/green/blue channels out of a theme colour, or null when the value
// is not a colour this file can take apart. Handles #rrggbb, the #rgb shorthand,
// and the rgb()/rgba() forms the colour editor writes whenever a swatch is given
// an alpha. The alpha is read past and discarded; every caller decides the opacity
// of what it is building. Exported because a canvas that wants to build its own
// gradient stops from a resolved theme colour needs the same three numbers, and a
// second parser beside this one is how the shorthand form gets missed in one of them.
// ---------------------------------------------------------------------------
export function readColorChannels(color: string): [number, number, number] | null {
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
// Utility: the same colour at a different lightness, as a #rrggbb string. Scales
// HSL lightness by the given factor and leaves hue and saturation where they were,
// so every shade a theme grows from its own colours keeps that colour's hue.
//
// Scaling lightness is what matches how the shipped themes were actually drawn:
// all nine write a hover that sits a step below their own button colour at
// essentially the same hue. Working in a perceptual space instead was measured
// and came out worse here, because it holds chroma flat while these ramps gain a
// little chroma as they darken.
//
// The two directions are not equally accurate, and that is a property of these
// palettes rather than of the maths. They turn about five degrees toward cyan as
// they lighten and barely three as they darken, so a step that keeps the hue
// reproduces the shipped darker shades to dE2000 0.8 and the lighter ones only to
// 2.3 - measured over every colour sharing the hue, not over a handful of
// formulas. A lighter step is still far closer than the alternative it replaces,
// which was one palette's blue frozen into every other theme.
// Unreadable input gives mid grey, matching hexToRgba above.
// ---------------------------------------------------------------------------
function scaleLightness(color: string, factor: number): string {
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
// The two named steps, so a call site says which way it is going. Both are the
// same scaling above; a factor below 1 goes down the ramp and above 1 goes up.
// ---------------------------------------------------------------------------
function darken(color: string, factor: number): string {
  return scaleLightness(color, factor);
}

function lighten(color: string, factor: number): string {
  return scaleLightness(color, factor);
}

// ---------------------------------------------------------------------------
// Which way a palette runs, so the shades below can step the right way without a
// theme having to declare it. A page whose text is lighter than its background is
// a dark theme and wants its accent shades lighter; a light page wants them
// darker. textPrimary and bgPrimary always carry a default, so there is always
// something to read, and the comparison stays right for a mid-grey page where a
// plain brightness threshold would not.
//
// Relative luminance is the WCAG definition. It is used only to order two
// colours, never reported, so nothing downstream depends on the exact number.
// ---------------------------------------------------------------------------
function toLinear(channel: number): number {
  const value = channel / 255;
  return value <= 0.03928 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4);
}

function fromLinear(value: number): number {
  const channel = value <= 0.0031308 ? value * 12.92 : 1.055 * Math.pow(value, 1 / 2.4) - 0.055;
  return Math.round(Math.max(0, Math.min(1, channel)) * 255);
}

function relativeLuminance(color: string): number {
  const channels = readColorChannels(color);
  if (!channels) {
    return 0;
  }

  const [red, green, blue] = channels.map(toLinear);

  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
}

function isDarkPalette(colors: Record<string, string>): boolean {
  return relativeLuminance(colors.textPrimary) > relativeLuminance(colors.bgPrimary);
}

function contrastRatio(color: string, ground: string): number {
  const [high, low] = [relativeLuminance(color), relativeLuminance(ground)].sort((a, b) => b - a);
  return (high + 0.05) / (low + 0.05);
}

// ---------------------------------------------------------------------------
// CIE Lab, so a shade can be moved in lightness without dragging its colorfulness
// down with it. Scaling HSL lightness - what `scaleLightness` above does, and what
// the shipped ramps were drawn with - loses chroma on the way down, and a warm hue
// that loses chroma is what people read as brown. The steps below therefore work in
// Lab: hue is held exactly, lightness is searched, and chroma is pushed back to the
// most the sRGB gamut will hold at whatever lightness wins.
// ---------------------------------------------------------------------------
function toLab(color: string): [number, number, number] | null {
  const channels = readColorChannels(color);
  if (!channels) {
    return null;
  }

  const [red, green, blue] = channels.map(toLinear);
  const x = (0.4124 * red + 0.3576 * green + 0.1805 * blue) / 0.95047;
  const y = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
  const z = (0.0193 * red + 0.1192 * green + 0.9505 * blue) / 1.08883;
  const f = (value: number): number =>
    value > 0.008856 ? Math.cbrt(value) : 7.787 * value + 16 / 116;
  const [fx, fy, fz] = [f(x), f(y), f(z)];

  return [116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)];
}

function fromLch(lightness: number, chroma: number, hue: number): string {
  const radians = (hue * Math.PI) / 180;
  const fy = (lightness + 16) / 116;
  const fx = fy + (chroma * Math.cos(radians)) / 500;
  const fz = fy - (chroma * Math.sin(radians)) / 200;
  const inverse = (value: number): number =>
    value ** 3 > 0.008856 ? value ** 3 : (value - 16 / 116) / 7.787;
  const [x, y, z] = [0.95047 * inverse(fx), inverse(fy), 1.08883 * inverse(fz)];
  const red = 3.2406 * x - 1.5372 * y - 0.4986 * z;
  const green = -0.9689 * x + 1.8758 * y + 0.0415 * z;
  const blue = 0.0557 * x - 0.204 * y + 1.057 * z;

  return `#${[red, green, blue]
    .map((channel) => fromLinear(channel).toString(16).padStart(2, '0'))
    .join('')}`;
}

// How much chroma the sRGB gamut holds for this hue at this lightness. The solid has
// no closed-form edge, so the edge is found by bisection: a request that clips comes
// back from fromLch at a different lightness or chroma than it was asked for, and
// that mismatch is the test.
function chromaCeiling(lightness: number, hue: number): number {
  let inside = 0;
  let outside = 140;
  for (let step = 0; step < 12; step++) {
    const chroma = (inside + outside) / 2;
    const lab = toLab(fromLch(lightness, chroma, hue));
    const [reached, axisA, axisB] = lab ?? [Infinity, 0, 0];
    const fits =
      lab !== null &&
      Math.abs(reached - lightness) <= 1 &&
      Math.abs(Math.hypot(axisA, axisB) - chroma) <= 2;
    if (fits) {
      inside = chroma;
    } else {
      outside = chroma;
    }
  }

  return inside;
}

// A color's hue, and how saturated it is as a FRACTION of what its own lightness
// allows. The fraction is the part worth carrying between lightnesses: hold it and a
// brand color that was at full chroma comes back at full chroma, while a near-neutral
// like the test gray stays gray instead of being pushed out to a hue it never had.
function readTone([lightness, axisA, axisB]: [number, number, number]): {
  hue: number;
  saturation: number;
} {
  const hue = (Math.atan2(axisB, axisA) * 180) / Math.PI;
  const ceiling = chromaCeiling(lightness, hue);

  return { hue, saturation: ceiling > 0 ? Math.min(1, Math.hypot(axisA, axisB) / ceiling) : 0 };
}

// Badge labels and the `.service-*` text classes. The brand value is tuned for chart
// slices and tiles, and on a near-white ground no vivid warm tone clears 4.5:1, so type
// takes a darker step and the chart keeps the brand value.
//
// The step goes DOWN from the color's own lightness and stops at the first one that
// clears the floor, so a brand color is moved as little as it can be. A value that
// already clears the floor is returned as it is - the dark blues and plums in the light
// palette are meant to be dark, and re-deriving them would repaint them brighter for no
// reason. Chroma is rebuilt at each step instead of being scaled away, which is the
// difference between a readable vermillion and the brown that plain darkening gives.
//
// A color already lighter than its ground is a dark theme's color on a dark surface
// and comes back untouched: it has its contrast already, and a hand-tuned dark theme
// should not be re-derived underneath its author.
export function readableTextColor(color: string, ground: string): string {
  const lab = toLab(color);
  if (!lab || relativeLuminance(color) > relativeLuminance(ground)) {
    return color;
  }
  if (contrastRatio(color, ground) >= 4.5) {
    return color;
  }

  const [baseLightness] = lab;
  const { hue, saturation } = readTone(lab);
  for (let lightness = Math.round(baseLightness); lightness >= 8; lightness -= 1) {
    const candidate = fromLch(lightness, saturation * chromaCeiling(lightness, hue), hue);
    if (contrastRatio(candidate, ground) >= 4.5) {
      return candidate;
    }
  }

  return color;
}

// Status dots and the other small solid marks. A dot is a shape, not type, so it answers
// to the 3:1 non-text floor rather than 4.5:1 - and a status color that has to be
// readable as words on a light page is dragged so dark that an 8px dot reads as a speck.
//
// This one searches UPWARD for the most colorful version the floor still allows, rather
// than the lightest: past the peak, chroma falls away again and the mark washes out, so
// the brightest legal value is the wrong answer and the most saturated one is the right
// one. Never darker than the color it came from.
export function indicatorColor(color: string, ground: string): string {
  const lab = toLab(color);
  if (!lab || relativeLuminance(color) > relativeLuminance(ground)) {
    return color;
  }

  const [baseLightness] = lab;
  const { hue, saturation } = readTone(lab);
  let result = color;
  let strongest = -1;
  for (let lightness = Math.round(baseLightness); lightness <= 92; lightness += 1) {
    const chroma = saturation * chromaCeiling(lightness, hue);
    const candidate = fromLch(lightness, chroma, hue);
    if (contrastRatio(candidate, ground) < 3) {
      break;
    }
    if (chroma > strongest) {
      strongest = chroma;
      result = candidate;
    }
  }

  return result;
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

  // Stop borrows the destructive red rather than carrying a red of its own, so a theme that
  // retunes only its delete colour keeps its stop buttons in step. Stopping a run is its own
  // job though, so the pair is separately settable for a theme that wants to part them [1]
  if (!result.actionStopBg) result.actionStopBg = result.actionDeleteBg;
  if (!result.actionStopHover) result.actionStopHover = result.actionDeleteHover;

  // The checkbox hover ring: a fixed 3px of the checkbox's own accent at 10%. Only the
  // geometry is fixed, so the colour still follows the theme
  if (!result.checkboxHoverShadow) {
    result.checkboxHoverShadow = `0 0 0 3px ${hexToRgba(result.checkboxAccent, 0.1)}`;
  }

  // Which way this palette steps, read once and used by the shades below
  const dark = isDarkPalette(result);

  // Accent text is the accent taken one step toward the page's own text, so links stand off the
  // ground rather than sinking into it. The shipped light theme takes the same step downward
  if (!result.textAccent) {
    result.textAccent = dark
      ? lighten(result.primaryColor, 1.18)
      : darken(result.primaryColor, 0.84);
  }

  // An info panel's fill and its words, both shaded from info rather than the accent because that
  // is what every shipped theme does by hand, and because deriving only one of them would put one
  // hue on the badge and another on the text sitting in it. They step opposite ways: the fill
  // sinks toward the page, the words climb away from the fill.
  //
  // The words take a bigger step than a link does, and on a light page a much bigger one. Badge
  // text has to clear the tinted ground under it rather than the page, and a bright hue such as
  // yellow or pale grey lightens far more than a blue does for the same move along the scale: a
  // link-sized step leaves that text at 2.2:1 on its own badge, where this one holds at least
  // 4.7:1 across every hue tried
  if (!result.infoBg) {
    result.infoBg = dark ? darken(result.info, 0.35) : lighten(result.info, 1.74);
  }
  if (!result.infoText) {
    result.infoText = dark ? lighten(result.info, 1.32) : darken(result.info, 0.55);
  }

  // The halo around the rocket, off the rocket rather than the accent so a theme that recolours
  // only its rocket keeps the two in step. A glow is brighter than the thing it surrounds on
  // either kind of page, so this is the one accent shade that does not flip with the palette
  if (!result.fireworkGlowColor) {
    result.fireworkGlowColor = lighten(result.fireworkRocketColor, 1.18);
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
