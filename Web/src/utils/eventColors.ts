/**
 * Utility functions for working with theme-based event colors.
 *
 * Event and tag colors use a colorIndex (1-8) that maps to CSS variables:
 * --theme-event-1 through --theme-event-8
 *
 * This ensures colors adapt when themes change.
 */

/**
 * Get the CSS variable for an event color index.
 * @param colorIndex - 1-8, the color index
 * @returns CSS variable string like "var(--theme-event-1)"
 */
export function getEventColorVar(colorIndex: number): string {
  const index = Math.max(1, Math.min(8, colorIndex));
  return `var(--theme-event-${index})`;
}

/**
 * Get inline styles for an event/tag badge with the color index.
 * Returns styles for background, text color, and border that adapt to the theme.
 * @param colorIndex - 1-8, the color index
 * @returns Style object for the badge
 */
export function getEventColorStyles(colorIndex: number): React.CSSProperties {
  const colorVar = getEventColorVar(colorIndex);
  return {
    backgroundColor: `color-mix(in srgb, ${colorVar} 20%, transparent)`,
    color: colorVar,
    border: `1px solid color-mix(in srgb, ${colorVar} 40%, transparent)`
  };
}

