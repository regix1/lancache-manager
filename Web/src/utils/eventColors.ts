/**
 * Utility functions for working with theme-based event colors.
 *
 * Event and tag colors use a colorIndex (1-8) that maps to CSS variables:
 * --theme-event-1 through --theme-event-8
 *
 * This ensures colors adapt when themes change.
 */

/** Pin a color index to the 1-8 range the theme defines variables for. */
export function clampEventColorIndex(colorIndex: number): number {
  return Math.max(1, Math.min(8, colorIndex));
}

/**
 * Get the CSS variable for an event color index.
 * @param colorIndex - 1-8, the color index
 * @returns CSS variable string like "var(--theme-event-1)"
 */
export function getEventColorVar(colorIndex: number): string {
  return `var(--theme-event-${clampEventColorIndex(colorIndex)})`;
}
