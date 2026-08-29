// Which Storage-section card the Management tab should open on and glow. The dashboard's stale
// scan chips set one and then ask App to switch tabs; ManagementTab is not mounted at that moment,
// so it cannot hear an event and reads the target on its own mount instead.
export type ScanJumpTarget = 'cacheFiles' | 'gameDetection';

let pendingTarget: ScanJumpTarget | null = null;

export const requestScanJump = (target: ScanJumpTarget): void => {
  pendingTarget = target;
};

// Reading leaves the target in place so it can be read from a render pass; the tab that acted on
// it clears it, which keeps opening Management by hand later from replaying the jump.
export const readScanJump = (): ScanJumpTarget | null => pendingTarget;

export const clearScanJump = (): void => {
  pendingTarget = null;
};
