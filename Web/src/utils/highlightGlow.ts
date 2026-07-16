export type HighlightGlowVariant = 'navigate' | 'subtle';

const GLOW_CLASS: Record<HighlightGlowVariant, string> = {
  navigate: 'glow-pulse',
  subtle: 'glow-pulse-subtle'
};

// Janitor bound only: slightly longer than the CSS animation (animations.css) so the
// class still gets removed when animationend never fires (element hidden mid-flash).
const GLOW_TIMEOUT_MS: Record<HighlightGlowVariant, number> = {
  navigate: 2600,
  subtle: 2000
};

const GLOW_ANIMATION_NAMES = new Set(['highlightGlowPulse', 'highlightGlowPulseSubtle']);

const activeCleanups = new WeakMap<HTMLElement, () => void>();

// Pulse a box-shadow glow around any element (button, card, accordion - the shadow
// follows the element's own border-radius). The class removes itself on animationend,
// so the glow always plays out fully even if the caller's state resets earlier.
export function flashHighlight(
  target: HTMLElement,
  variant: HighlightGlowVariant = 'navigate'
): void {
  activeCleanups.get(target)?.();
  target.classList.remove(GLOW_CLASS.navigate, GLOW_CLASS.subtle);
  // Reflow so removing and re-adding the class restarts an in-flight animation.
  void target.offsetWidth;

  const glowClass = GLOW_CLASS[variant];
  target.classList.add(glowClass);

  let timer = 0;
  const cleanup = (): void => {
    target.classList.remove(glowClass);
    target.removeEventListener('animationend', onAnimationEnd);
    window.clearTimeout(timer);
    activeCleanups.delete(target);
  };
  const onAnimationEnd = (event: AnimationEvent): void => {
    if (event.target === target && GLOW_ANIMATION_NAMES.has(event.animationName)) {
      cleanup();
    }
  };
  target.addEventListener('animationend', onAnimationEnd);
  timer = window.setTimeout(cleanup, GLOW_TIMEOUT_MS[variant]);
  activeCleanups.set(target, cleanup);
}
