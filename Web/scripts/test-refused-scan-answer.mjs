import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

/**
 * Where a person reads that a cache scan was refused while a client download writes to the cache:
 *
 *   - A popup that names a service follows that service's display style, like the service's own
 *     run card, so it never stacks a second style beside it.
 *   - The Disk Cache Management card renders the server's refusal sentence, but the menu item that
 *     would fetch it is disabled while the download runs, so the sentence never arrives.
 *
 * Both are expressions inside a component, never exported, so each is lifted out of the file it
 * ships in and run here rather than restated.
 */

const BAR = 'src/components/common/UniversalNotificationBar.tsx';
const CARD = 'src/components/features/management/cache/CacheManager.tsx';

/**
 * The notification bar's per-notification classifier, with its free variables supplied and the
 * styles already known; it answers the one card it draws.
 */
const classifier = (displayModes, scheduledTypeToServiceKey = {}) => {
  const classify = bindLifted(liftHookCallback(BAR, 'flatMap', 'condensedByService'), {
    SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY: scheduledTypeToServiceKey,
    modes: displayModes,
    defaultMode: 'full',
    ready: true,
    platformDisplayModeKey: () => '',
    isTerminalNotificationStatus: (status) =>
      ['completed', 'failed', 'cancelled', 'skipped'].includes(status),
    fullOrder: 0,
    isMobile: false,
    MOBILE_FULL_CARD_CAP: 2
  });
  return (notification) => classify(notification)[0];
};

test('a popup that names a service follows that service set to the compact bar', () => {
  const classify = classifier({ cacheReconciliation: 'condensed' });

  const popup = classify({
    type: 'generic',
    status: 'failed',
    message: 'A client download is writing to the cache right now.',
    details: { notificationType: 'error', serviceKey: 'cacheReconciliation' }
  });

  assert.equal(popup.condensed, true);
});

test('a routine run that is skipped stays on the compact bar the schedule asked for', () => {
  const classify = classifier(
    { cacheReconciliation: 'condensed' },
    { eviction_scan: 'cacheReconciliation' }
  );

  const scheduled = classify({
    type: 'eviction_scan',
    status: 'skipped',
    message: 'A client download is writing to the cache right now.',
    details: {}
  });

  assert.equal(scheduled.condensed, true);
});

/** The sole binary expression in the card that uses `operator` and mentions `cacheSizeDenialReason`. */
const cardBinary = (operator) => {
  const sourceFile = parseSource(CARD, typescript.ScriptKind.TSX);
  const node = findSoleNode(sourceFile, `cache notice ${operator}`, (candidate) => {
    if (!typescript.isBinaryExpression(candidate)) return false;
    if (candidate.operatorToken.kind !== operator) return false;
    return candidate.getText(sourceFile).includes('cacheSizeDenialReason');
  });
  return { node, sourceFile };
};

/** Whether the card's yellow notice renders, for the state the card is in. */
const noticeShown = (state) => {
  const { node, sourceFile } = cardBinary(typescript.SyntaxKind.AmpersandAmpersandToken);
  return bindLifted(`() => (${node.left.getText(sourceFile)})`, state)();
};

test('the cache card explains a refresh the server refused', () => {
  const serverSentence = 'A client download is writing to the cache right now.';

  assert.equal(
    noticeShown({ cacheSizeDenialReason: serverSentence, scanGate: { blocked: false } }),
    serverSentence
  );
});

test('the cache card stays quiet when the refresh is available', () => {
  assert.equal(
    Boolean(noticeShown({ cacheSizeDenialReason: null, scanGate: { blocked: false } })),
    false
  );
});

test('a download in flight raises no notice on this card', () => {
  // The refresh is disabled and explains itself on hover, which is what the corruption, detection
  // and eviction cards do with the same gate. This card used to raise a banner for it as well and
  // was alone in doing so, which read as a warning about the cache rather than about one button.
  assert.equal(
    Boolean(noticeShown({ cacheSizeDenialReason: null, scanGate: { blocked: true } })),
    false
  );
});
