import { prefersReducedMotion } from '@components/features/management/status-check/helpers';

// Longer than the usual 300 ms UI motion on purpose: the travel can span a whole page, and this is
// about as long as the browsers' own smooth scroll takes over that distance.
const GLIDE_MS = 450;

// Moves a scroll box to `to` one frame at a time and returns the stop. A native smooth scroll cannot
// be stopped in Firefox, which ignores a scroll to the current position, so a wheel, key or click would
// not end it. Here a stop only skips the next frame, and the browser applies the user's input as is.
// `to` must already be inside the scroll range: a target past the end would reach the end early and
// sit still for the rest of the travel.
export function glideScroll(owner: HTMLElement, to: number, onDone?: () => void): () => void {
  const from = owner.scrollTop;
  if (to === from || prefersReducedMotion()) {
    owner.scrollTop = to;
    onDone?.();
    return () => undefined;
  }
  const startedAt = performance.now();
  const advance = (now: number): void => {
    // A frame's timestamp can precede startedAt.
    const elapsed = Math.min(1, Math.max(0, (now - startedAt) / GLIDE_MS));
    owner.scrollTop = from + (to - from) * (1 - (1 - elapsed) ** 3);
    if (elapsed < 1) {
      frame = requestAnimationFrame(advance);
      return;
    }
    onDone?.();
  };
  let frame = requestAnimationFrame(advance);
  return () => cancelAnimationFrame(frame);
}
