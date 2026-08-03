/**
 * Keeps one axis of a floating box inside the viewport.
 *
 * `desired` is where the box's near edge wants to sit, `size` is how long the box
 * is on that axis, `viewportSize` is the viewport's length on the same axis and
 * `gutter` is the smallest gap to leave at either end. All four are viewport CSS
 * pixels: a body-portalled box that is positioned in document coordinates must
 * add `window.scrollX` / `window.scrollY` after clamping, not before.
 *
 * When the box is longer than the space between the two gutters no position
 * satisfies both ends, and the near end wins, so the result is `gutter`.
 */
export function clampToViewport(
  desired: number,
  size: number,
  viewportSize: number,
  gutter: number
): number {
  return Math.max(gutter, Math.min(desired, viewportSize - size - gutter));
}
