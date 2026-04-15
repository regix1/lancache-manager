/**
 * Tiny performance timing tracker for click-to-render latency profiling.
 * Single active session at a time — last `start` wins.
 *
 * Usage:
 *   start('range=24h')   → kicks off a session, logs `[timing] range=24h START`
 *   mark('fetch-start')  → logs `[timing] range=24h → fetch-start +Xms`
 *   mark('fetch-done')   → logs `[timing] range=24h → fetch-done +Xms`
 *   end('render-done')   → logs `[timing] range=24h → render-done +Xms — DONE`
 */

let startedAt: number | null = null;
let activeLabel: string | null = null;

export function start(label: string): void {
  startedAt = performance.now();
  activeLabel = label;
  // eslint-disable-next-line no-console
  console.log(`[timing] ${label} START`);
}

export function mark(checkpoint: string): void {
  if (startedAt === null || activeLabel === null) return;
  const ms = (performance.now() - startedAt).toFixed(1);
  // eslint-disable-next-line no-console
  console.log(`[timing] ${activeLabel} → ${checkpoint} +${ms}ms`);
}

export function end(checkpoint: string): void {
  if (startedAt === null || activeLabel === null) return;
  const ms = (performance.now() - startedAt).toFixed(1);
  // eslint-disable-next-line no-console
  console.log(`[timing] ${activeLabel} → ${checkpoint} +${ms}ms — DONE`);
  startedAt = null;
  activeLabel = null;
}

/** Whether a timing session is currently active. */
export function isActive(): boolean {
  return startedAt !== null;
}
