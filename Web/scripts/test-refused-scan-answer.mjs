import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

/**
 * Two places tell a person that a cache scan was refused while a client download writes to the
 * cache, and both used to say nothing at all on the screen the person was looking at:
 *
 *   - Run Now on a schedule set to the compact bar answered a refusal with a coloured line that
 *     carries no reason and clears itself, because the refused run has no card of its own to open.
 *   - The Disk Cache Management card renders the server's refusal sentence, but the menu item that
 *     would fetch it is disabled while the download runs, so the sentence never arrives.
 *
 * Both are expressions inside a component, never exported, so each is lifted out of the file it
 * ships in and run here rather than restated.
 */

const BAR = 'src/components/common/UniversalNotificationBar.tsx';
const CARD = 'src/components/features/management/cache/CacheManager.tsx';

/** The notification bar's per-notification classifier, with its free variables supplied. */
const classifier = (displayModes, scheduledTypeToServiceKey = {}) =>
  bindLifted(liftHookCallback(BAR, 'map', 'condensedByService'), {
    SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY: scheduledTypeToServiceKey,
    displayModes,
    fullOrder: 0,
    isMobile: false,
    MOBILE_FULL_CARD_CAP: 2
  });

test('a refused Run Now keeps its card on a service set to the compact bar', () => {
  const classify = classifier({ cacheReconciliation: 'condensed' });

  const refused = classify({
    type: 'generic',
    status: 'skipped',
    message: 'A client download is writing to the cache right now.',
    details: { notificationType: 'warning', serviceKey: 'cacheReconciliation' }
  });

  assert.equal(refused.condensed, false);
});

test('an accepted Run Now still folds into its service line', () => {
  const classify = classifier({ cacheReconciliation: 'condensed' });

  const started = classify({
    type: 'generic',
    status: 'completed',
    message: 'Started Eviction Scan',
    details: { notificationType: 'success', serviceKey: 'cacheReconciliation' }
  });

  assert.equal(started.condensed, true);
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

/** The sentence that notice carries, for a card state. */
const noticeText = (cacheSizeDenialReason, tooltip = '') => {
  const { node, sourceFile } = cardBinary(typescript.SyntaxKind.QuestionQuestionToken);
  return bindLifted(`() => (${node.getText(sourceFile)})`, {
    cacheSizeDenialReason,
    scanGate: { tooltip }
  })();
};

test('the cache card explains a refused refresh with the gate’s own sentence', () => {
  const trackerSilent =
    'The download tracker has not reported yet, so a scan cannot tell whether the cache is being written to. Try again in a few seconds.';

  assert.equal(noticeShown({ cacheSizeDenialReason: null, scanGate: { blocked: true } }), true);
  // The refresh is refused for two different reasons and only the server knows which, so the card
  // repeats what the gate said instead of asserting a download.
  assert.equal(noticeText(null, trackerSilent), trackerSilent);
});

test('the cache card stays quiet when the refresh is available', () => {
  assert.equal(
    Boolean(noticeShown({ cacheSizeDenialReason: null, scanGate: { blocked: false } })),
    false
  );
});

test('a refusal the server did answer keeps the server sentence', () => {
  const serverSentence = 'A client download is writing to the cache right now.';

  assert.equal(
    noticeShown({ cacheSizeDenialReason: serverSentence, scanGate: { blocked: false } }),
    serverSentence
  );
  assert.equal(noticeText(serverSentence), serverSentence);
});
