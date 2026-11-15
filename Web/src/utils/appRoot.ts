// Modals and the full-screen setup gates hide the app root while they are drawn, and they overlap:
// a gate fading out while the next screen's gate opens, or a modal opening on the page a gate fades
// onto. Each overlay holds the root once and releases it once, and the root comes back only when
// the last holder is gone. A save and restore per overlay wrote one overlay's state back after the
// other had left, which kept the page hidden and unclickable once both were gone. The page scroll
// lock (`modal-open` on `<html>`) is held the same way by every gate, modal and drawer, so the
// page behind stays still until the last of them is gone.
let hiddenHolds = 0;
let inertHolds = 0;
let scrollHolds = 0;

/** Locks the page's scroll until the returned release runs. */
export const holdScrollLock = (): (() => void) => {
  scrollHolds += 1;
  document.documentElement.classList.add('modal-open');
  return () => {
    scrollHolds -= 1;
    if (scrollHolds === 0) document.documentElement.classList.remove('modal-open');
  };
};

/**
 * Hides the app root from assistive technology until the returned release runs. `inert` also takes
 * the page out of pointer and keyboard reach, for an overlay that covers all of it.
 */
export const holdAppRoot = (inert: boolean): (() => void) => {
  const appRoot = document.getElementById('root');
  hiddenHolds += 1;
  if (inert) inertHolds += 1;
  if (appRoot) {
    appRoot.setAttribute('aria-hidden', 'true');
    if (inert) appRoot.inert = true;
  }
  return () => {
    hiddenHolds -= 1;
    if (inert) inertHolds -= 1;
    if (!appRoot) return;
    if (hiddenHolds === 0) appRoot.removeAttribute('aria-hidden');
    if (inert && inertHolds === 0) appRoot.inert = false;
  };
};
