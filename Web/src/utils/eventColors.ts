/**
 * Utility functions for working with theme-based event colors.
 *
 * Event and tag colors use a colorIndex (1-8) that maps to CSS variables:
 * --theme-event-1 through --theme-event-8
 *
 * This ensures colors adapt when themes change.
 */

/** The colour indexes the theme defines event variables for. */
type EventColorIndex = 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8;

/** Pin a color index to the 1-8 range the theme defines variables for. */
export function clampEventColorIndex(colorIndex: number): EventColorIndex {
  return Math.max(1, Math.min(8, colorIndex)) as EventColorIndex;
}

/**
 * Theme colour variables that also define the suffixed tier variables below. The union is
 * closed on purpose: a name the theme never declares is a compile error here rather than a
 * `var()` that resolves to nothing and silently paints no colour.
 */
export type ColorToken =
  | `--theme-event-${EventColorIndex}`
  | '--theme-primary'
  | '--theme-success'
  | '--theme-text-muted';

/**
 * The suffixed variants the theme defines alongside a colour variable. Every theme colour
 * that gets a tier spells the suffixes the same way, so the same names work for the event
 * colours and for the status/primary/text colours the shared UI components pass in.
 */
export type ColorTier =
  | 'subtle'
  | 'muted'
  | 'emphasis'
  | 'intense'
  | 'strong'
  | 'on-bg'
  | 'on-bg-soft'
  | 'on-bg-strong';

/**
 * Build the CSS reference for a colour token, optionally at one of its tiers.
 * `var()` takes a literal name, so the name has to be assembled here rather than in CSS.
 * @returns a string like "var(--theme-event-1)" or "var(--theme-event-1-subtle)"
 */
export function themeColorVar(token: ColorToken, tier?: ColorTier): string {
  return `var(${token}${tier ? `-${tier}` : ''})`;
}

/** The colour token for an event colour index. */
export function eventColorToken(colorIndex: number): ColorToken {
  return `--theme-event-${clampEventColorIndex(colorIndex)}`;
}

/**
 * Get the CSS variable for an event color index.
 * @param colorIndex - 1-8, the color index
 * @returns CSS variable string like "var(--theme-event-1)"
 */
export function getEventColorVar(colorIndex: number): string {
  return themeColorVar(eventColorToken(colorIndex));
}
