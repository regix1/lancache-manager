import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import {
  bindLifted,
  collectNodes,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * A count the server refuses, such as an unmapped count over an empty or unmounted cache, ends
 * with the refusal's key. The removal dialogs say why the count failed instead of only that it
 * failed, and a progress line left from the running count is never shown as the reason.
 *
 * The count effect, the dialog line and the reason passed to the unmapped dialog are lifted out of
 * the component as shipped.
 */

const detectorPath = 'src/components/features/management/game-detection/GameCacheDetector.tsx';

const effectSource = liftHookCallback(
  detectorPath,
  'useEffect',
  'Could not start the cache file count'
);

const sourceFile = parseSource(detectorPath, typescript.ScriptKind.TSX);

const countStatusMessageSource = (() => {
  const declaration = findSoleNode(
    sourceFile,
    'countStatusMessage declaration',
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'countStatusMessage'
  );
  return `() => (${declaration.initializer.getText(sourceFile)})`;
})();

const failureReasonSource = (() => {
  const attribute = findSoleNode(
    sourceFile,
    'failureReason attribute',
    (node) => typescript.isJsxAttribute(node) && node.name.getText(sourceFile) === 'failureReason'
  );
  return `() => (${attribute.initializer.expression.getText(sourceFile)})`;
})();

const flush = () => new Promise((resolve) => setTimeout(resolve, 20));

// Runs the unmapped dialog's count against a server whose status answers come from `answers`, one
// per poll; the last answer repeats.
const runCount = async (answers) => {
  const stages = [];
  let failed = false;
  let poll = 0;
  const api = {
    startUnmappedCacheFileCount: () => Promise.resolve({ operationId: 'count-1' }),
    cancelOperation: () => Promise.resolve(),
    getCacheFileCountStatus: () => {
      const answer = answers[Math.min(poll++, answers.length - 1)];
      return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
    }
  };
  const close = bindLifted(effectSource, {
    gameToRemove: null,
    serviceToRemove: null,
    unmappedRemovalOpen: true,
    ApiService: api,
    classifyGameFromCacheInfo: () => {
      throw new Error('only the unmapped count runs here');
    },
    setCacheFileCount: () => undefined,
    setCountStage: (stage) => stages.push(stage),
    setCountFailed: (next) => {
      failed = next;
    },
    setCacheFileBytes: () => undefined,
    setCountedUnmappedServices: () => undefined,
    setUnmappedRemovalError: () => undefined,
    COUNT_POLL_INTERVAL_MS: 5,
    COUNT_CANCEL_RETRY_MS: 60
  })();
  await flush();
  await flush();
  close();
  return { stage: stages.at(-1), failed };
};

const messageFor = (countStage, countFailed) =>
  bindLifted(countStatusMessageSource, {
    countStage,
    countFailed,
    translateRecoveryStage: (key, _context, fallbackKey) => (key ? `t:${key}` : `t:${fallbackKey}`)
  })();

test('a count refused for an empty cache ends with that reason, and the dialogs show it', async () => {
  const { stage, failed } = await runCount([
    { operationId: 'count-1', isProcessing: true, stageKey: 'signalr.unmappedRemove.counting' },
    { operationId: 'count-1', isProcessing: false, stageKey: 'errors.unmappedRemoval.cacheEmpty' }
  ]);

  assert.equal(failed, true);
  assert.deepEqual(stage, { key: 'errors.unmappedRemoval.cacheEmpty', context: {} });

  const message = messageFor(stage, true);
  assert.equal(message, 't:errors.unmappedRemoval.cacheEmpty');
  const reason = bindLifted(failureReasonSource, {
    countFailed: true,
    countStage: stage,
    countStatusMessage: message
  })();
  assert.equal(reason, 't:errors.unmappedRemoval.cacheEmpty');
});

test('a count that ends with no reason, or whose status request fails, shows the plain failure', async () => {
  for (const last of [{ operationId: 'count-1', isProcessing: false }, new Error('network down')]) {
    const { stage, failed } = await runCount([
      { operationId: 'count-1', isProcessing: true, stageKey: 'signalr.unmappedRemove.counting' },
      last
    ]);

    assert.equal(failed, true);
    // The progress line is not a reason.
    assert.equal(stage, null);
    assert.equal(messageFor(stage, true), 't:modals.cacheRemoval.countFailed');
    assert.equal(
      bindLifted(failureReasonSource, {
        countFailed: true,
        countStage: stage,
        countStatusMessage: messageFor(stage, true)
      })(),
      null
    );
  }
});

test('while the count runs, the line is its progress', () => {
  assert.equal(
    messageFor({ key: 'signalr.unmappedRemove.counting', context: {} }, false),
    't:signalr.unmappedRemove.counting'
  );
  assert.equal(messageFor(null, false), 't:modals.cacheRemoval.counting');
});

test('each reason a game or service count can end with still says there is no number to confirm against', async () => {
  // The game and service dialogs show the server's reason in place of their own failure line, so
  // the reason has to carry what that line told the person.
  const noNumber = { en: 'no number to confirm against', zh: '没有可供确认的数字' };
  for (const [language, phrase] of Object.entries(noNumber)) {
    const strings = JSON.parse(
      await readFile(new URL(`../src/i18n/locales/${language}.json`, import.meta.url), 'utf8')
    );
    const lookup = (key) => key.split('.').reduce((node, part) => node?.[part], strings);
    assert.ok(lookup('modals.cacheRemoval.countFailed').includes(phrase), language);
    for (const reason of ['errors.cache.countFailed', 'errors.cache.countStopped']) {
      assert.equal(typeof lookup(reason), 'string', `${language} ${reason}`);
      assert.ok(lookup(reason).includes(phrase), `${language} ${reason}`);
    }
  }
});

const renderRemovalDialog = (CacheRemovalModal, onRetry) =>
  renderToStaticMarkup(
    React.createElement(CacheRemovalModal, {
      target: {
        type: 'game',
        data: { game_name: 'Game', cache_files_found: 1, total_size_bytes: 1, depot_ids: [] }
      },
      onClose: () => undefined,
      onConfirm: () => undefined,
      fileBytes: null,
      statusMessage: 't:errors.cache.countStopped',
      confirming: false,
      error: null,
      onRetry
    })
  );

test('a game or service dialog whose count failed offers Retry, and pressing it counts again', async () => {
  // The dialog compiled as shipped. The modal shell, the alert and the button are replaced by
  // plain elements, and each rendered button's click handler is kept so the test can press it.
  const react = import.meta.resolve('react');
  const element = (source) => moduleUrl(`import React from '${react}';\n${source}`);
  const buttonUrl = element(
    'export const pressed = [];\n' +
      'export const Button = ({ onClick, children }) => {\n' +
      '  pressed.push(onClick);\n' +
      "  return React.createElement('button', null, children);\n" +
      '};'
  );
  const modalUrl = await compileToUrl(
    '../src/components/modals/cache/CacheRemovalModal.tsx',
    {
      react,
      'react-i18next': moduleUrl('export const useTranslation = () => ({ t: (key) => key });'),
      '@components/common/ConfirmationModal': element(
        "export const ConfirmationModal = ({ children }) => React.createElement('div', null, children);"
      ),
      '@components/ui/Alert': element(
        "export const Alert = ({ action, children }) => React.createElement('div', null, children, action);"
      ),
      '@components/ui/Button': buttonUrl,
      '@utils/formatters': moduleUrl(
        'export const formatBytes = (value) => `${value} B`;\n' +
          'export const formatCount = (value) => `${value}`;'
      ),
      '@hooks/useHeldValue': moduleUrl('export const useHeldValue = (value) => value;')
    },
    { jsx: typescript.JsxEmit.React }
  );
  const { default: CacheRemovalModal } = await import(modalUrl);
  const { pressed } = await import(buttonUrl);

  let retries = 0;
  const markup = renderRemovalDialog(CacheRemovalModal, () => {
    retries += 1;
  });

  assert.ok(markup.includes('t:errors.cache.countStopped'), markup);
  assert.ok(markup.includes('<button>common.retry</button>'), markup);
  pressed.at(-1)();
  assert.equal(retries, 1);

  // The evicted-records dialog runs no count and passes no retry.
  assert.ok(!renderRemovalDialog(CacheRemovalModal, undefined).includes('common.retry'));
});

test('the game and service dialogs get a retry only while the count has failed', () => {
  const dialogs = collectNodes(
    sourceFile,
    (node) =>
      typescript.isJsxSelfClosingElement(node) &&
      node.tagName.getText(sourceFile) === 'CacheRemovalModal'
  );
  assert.equal(dialogs.length, 2);
  for (const dialog of dialogs) {
    const onRetry = dialog.attributes.properties.find(
      (attribute) =>
        typescript.isJsxAttribute(attribute) && attribute.name.getText(sourceFile) === 'onRetry'
    );
    assert.ok(onRetry, dialog.getText(sourceFile));
    const retryFor = (countFailed, setCountAttempt) =>
      bindLifted(`() => (${onRetry.initializer.expression.getText(sourceFile)})`, {
        countFailed,
        countAttempt: 2,
        setCountAttempt
      })();

    assert.equal(
      retryFor(false, () => undefined),
      undefined
    );
    const attempts = [];
    retryFor(true, (next) => attempts.push(next))();
    assert.deepEqual(attempts, [3]);
  }
});
