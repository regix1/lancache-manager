import { useCallback, useEffect, useLayoutEffect, useRef, useState, type RefObject } from 'react';

interface ExpansionScrollOptions {
  contentRoot: RefObject<HTMLElement | null>;
  enabled: boolean;
  expandedItem: string | null;
  membersReady: boolean;
  resetKey: string;
  view: 'compact' | 'card' | 'normal' | 'retro';
}

interface ScrollIntent {
  cancelWait: Promise<void>;
  frame: number | null;
  groupId: string;
  movementStarted: boolean;
  owner: HTMLElement | null;
  removeInputListeners: (() => void) | null;
  resolveCancel: () => void;
  resolveFrame: (() => void) | null;
  sequence: number;
}

const EDGE_GAP = 16;
const MOVEMENT_EPSILON = 0.5;
const NAVIGATION_KEYS = new Set(['ArrowDown', 'ArrowUp', 'End', 'Home', 'PageDown', 'PageUp', ' ']);

const clearFrame = (intent: ScrollIntent): void => {
  if (intent.frame !== null) {
    cancelAnimationFrame(intent.frame);
    intent.frame = null;
  }
  const resolve = intent.resolveFrame;
  intent.resolveFrame = null;
  resolve?.();
};

const documentOwner = (): HTMLElement | null => document.scrollingElement as HTMLElement | null;

const ownerTop = (owner: HTMLElement): number => owner.scrollTop;

const moveOwner = (owner: HTMLElement, top: number, behavior: ScrollBehavior): void => {
  if (owner === documentOwner()) {
    window.scrollTo({ top, behavior });
    return;
  }
  owner.scrollTo({ top, behavior });
};

const findGroup = (root: HTMLElement, groupId: string): HTMLElement | null =>
  Array.from(root.querySelectorAll<HTMLElement>('[data-download-group-id]')).find(
    (element) => element.dataset.downloadGroupId === groupId
  ) ?? null;

const finiteAnimations = (target: HTMLElement): Animation[] =>
  typeof target.getAnimations === 'function'
    ? target.getAnimations({ subtree: true }).filter((animation) => {
        const endTime = animation.effect?.getComputedTiming().endTime;
        return typeof endTime === 'number' && Number.isFinite(endTime);
      })
    : [];

const visibleBounds = (owner: HTMLElement): { bottom: number; height: number; top: number } => {
  if (owner !== documentOwner()) {
    const rect = owner.getBoundingClientRect();
    const top = rect.top + EDGE_GAP;
    const bottom = rect.bottom - EDGE_GAP;
    return { top, bottom, height: Math.max(0, bottom - top) };
  }

  const navigation = document.querySelector<HTMLElement>('nav.sticky');
  let navigationBottom = 0;
  if (navigation) {
    const position = window.getComputedStyle(navigation).position;
    const rect = navigation.getBoundingClientRect();
    if ((position === 'fixed' || position === 'sticky') && rect.top <= 0 && rect.bottom > 0) {
      navigationBottom = rect.bottom;
    }
  }

  const top = Math.max(0, navigationBottom) + EDGE_GAP;
  const bottom = window.innerHeight - EDGE_GAP;
  return { top, bottom, height: Math.max(0, bottom - top) };
};

export const useExpansionScroll = ({
  contentRoot,
  enabled,
  expandedItem,
  membersReady,
  resetKey,
  view
}: ExpansionScrollOptions) => {
  const intentRef = useRef<ScrollIntent | null>(null);
  const sequenceRef = useRef(0);
  const [requestVersion, setRequestVersion] = useState(0);

  const finishIntent = useCallback((intent: ScrollIntent): void => {
    if (intentRef.current !== intent) return;
    clearFrame(intent);
    intent.resolveCancel();
    intent.removeInputListeners?.();
    intent.removeInputListeners = null;
    intentRef.current = null;
  }, []);

  const cancelScroll = useCallback((): void => {
    const intent = intentRef.current;
    if (!intent) return;
    clearFrame(intent);
    intent.resolveCancel();
    intent.removeInputListeners?.();
    intent.removeInputListeners = null;
    if (intent.movementStarted && intent.owner) {
      moveOwner(intent.owner, ownerTop(intent.owner), 'auto');
    }
    intentRef.current = null;
  }, []);

  const requestScroll = useCallback(
    (groupId: string): void => {
      cancelScroll();
      if (!enabled || (view !== 'compact' && view !== 'normal')) return;

      let resolveCancel = (): void => undefined;
      const cancelWait = new Promise<void>((resolve) => {
        resolveCancel = resolve;
      });
      const intent: ScrollIntent = {
        cancelWait,
        frame: null,
        groupId,
        movementStarted: false,
        owner: null,
        removeInputListeners: null,
        resolveCancel,
        resolveFrame: null,
        sequence: ++sequenceRef.current
      };

      const interrupt = (): void => cancelScroll();
      const interruptKey = (event: KeyboardEvent): void => {
        if (NAVIGATION_KEYS.has(event.key)) cancelScroll();
      };
      window.addEventListener('wheel', interrupt, { capture: true, passive: true });
      window.addEventListener('touchstart', interrupt, { capture: true, passive: true });
      window.addEventListener('pointerdown', interrupt, { capture: true, passive: true });
      window.addEventListener('keydown', interruptKey, true);
      intent.removeInputListeners = () => {
        window.removeEventListener('wheel', interrupt, true);
        window.removeEventListener('touchstart', interrupt, true);
        window.removeEventListener('pointerdown', interrupt, true);
        window.removeEventListener('keydown', interruptKey, true);
      };

      intentRef.current = intent;
      setRequestVersion(intent.sequence);
    },
    [cancelScroll, enabled, view]
  );

  useLayoutEffect(() => {
    cancelScroll();
  }, [cancelScroll, enabled, resetKey, view]);

  useEffect(() => () => cancelScroll(), [cancelScroll]);

  useEffect(() => {
    const intent = intentRef.current;
    if (!intent) return;
    if (expandedItem !== intent.groupId) {
      cancelScroll();
      return;
    }
    if (!membersReady || !contentRoot.current) {
      return;
    }

    const current = (): boolean => intentRef.current === intent;
    const nextFrame = (): Promise<void> =>
      new Promise((resolve) => {
        intent.resolveFrame = resolve;
        intent.frame = requestAnimationFrame(() => {
          intent.frame = null;
          intent.resolveFrame = null;
          resolve();
        });
      });

    const waitForGroup = async (): Promise<HTMLElement | null> => {
      while (current()) {
        const root = contentRoot.current;
        const target = root ? findGroup(root, intent.groupId) : null;
        if (target) return target;
        await nextFrame();
      }
      return null;
    };

    const waitForStableLayout = async (
      target: HTMLElement,
      owner: HTMLElement
    ): Promise<boolean> => {
      let previous: { bottom: number; ownerHeight: number; top: number } | null = null;
      while (current()) {
        await nextFrame();
        if (!current()) return false;
        const rect = target.getBoundingClientRect();
        const sample = { bottom: rect.bottom, ownerHeight: owner.scrollHeight, top: rect.top };
        if (
          previous &&
          Math.abs(sample.top - previous.top) <= MOVEMENT_EPSILON &&
          Math.abs(sample.bottom - previous.bottom) <= MOVEMENT_EPSILON &&
          Math.abs(sample.ownerHeight - previous.ownerHeight) <= MOVEMENT_EPSILON
        ) {
          return true;
        }
        previous = sample;
      }
      return false;
    };

    const watchMovement = (owner: HTMLElement, targetTop: number, startTop: number): void => {
      let lastTop = startTop;
      let moved = false;
      let stableFrames = 0;
      const check = (): void => {
        if (!current()) return;
        const top = ownerTop(owner);
        const changed = Math.abs(top - lastTop) > MOVEMENT_EPSILON;
        if (changed) moved = true;
        const settled = Math.abs(top - targetTop) <= MOVEMENT_EPSILON || (moved && !changed);
        stableFrames = settled ? stableFrames + 1 : 0;
        if (stableFrames >= 2) {
          finishIntent(intent);
          return;
        }
        lastTop = top;
        intent.frame = requestAnimationFrame(check);
      };
      intent.frame = requestAnimationFrame(check);
    };

    const fulfill = async (): Promise<void> => {
      const target = await waitForGroup();
      if (!target || !current()) return;

      const animations = finiteAnimations(target);
      if (animations.length > 0) {
        await Promise.race([
          Promise.allSettled(animations.map((animation) => animation.finished)),
          intent.cancelWait
        ]);
      }
      if (!current()) return;

      const owner = target.closest<HTMLElement>('.virtual-list-parent') ?? documentOwner();
      if (!owner || !(await waitForStableLayout(target, owner)) || !current()) return;

      const rect = target.getBoundingClientRect();
      const bounds = visibleBounds(owner);
      if (rect.top >= bounds.top && rect.bottom <= bounds.bottom) {
        finishIntent(intent);
        return;
      }

      let change: number;
      if (rect.height > bounds.height || rect.top < bounds.top) {
        change = rect.top - bounds.top;
      } else {
        change = rect.bottom - bounds.bottom;
      }

      const viewportHeight = owner === documentOwner() ? window.innerHeight : owner.clientHeight;
      const maximum = Math.max(0, owner.scrollHeight - viewportHeight);
      const startTop = ownerTop(owner);
      const targetTop = Math.min(maximum, Math.max(0, startTop + change));
      if (Math.abs(targetTop - startTop) <= MOVEMENT_EPSILON) {
        finishIntent(intent);
        return;
      }

      intent.owner = owner;
      intent.movementStarted = true;
      const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      moveOwner(owner, targetTop, reduceMotion ? 'auto' : 'smooth');
      watchMovement(owner, targetTop, startTop);
    };

    void fulfill();
  }, [cancelScroll, contentRoot, expandedItem, finishIntent, membersReady, requestVersion]);

  return { cancelScroll, requestScroll };
};
