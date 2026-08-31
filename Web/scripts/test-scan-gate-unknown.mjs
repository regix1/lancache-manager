import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
  compileToUrl,
  findSoleNode,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * A cache scan has three answers, not two: a download is writing, nothing is writing, and the
 * server has not said yet. The third one used to be stored as the second, so between first paint
 * and the first response every scan control read as ready to use on a claim nothing had made.
 *
 * The hook is compiled and driven here for real, against a translation stub that hands back the
 * key, so the assertions name which sentence each of the three states shows. The five controls
 * that read it are then checked against the source they ship in, because "not yet known" only
 * stays honest for as long as all five keep asking the same question.
 *
 * The same defect sat on the setup status, which the Game Cache scans also read. A status call
 * that fails falls back to a placeholder reading `hasProcessedLogs: false`, so collapsing it to a
 * boolean told a person to process their logs first on an install whose logs were processed, and
 * kept telling them for as long as the call kept failing. Those cases are driven here too.
 */

const tick = () => new Promise((resolve) => setImmediate(resolve));

/**
 * The smallest React that can run this hook: ordered slots for `useState`, `useCallback` and
 * `useEffect`, a post-render effect flush, and a re-render when a setter changes a slot. A
 * data-URL import resolves no bare specifier, so the hook is compiled against this in place of
 * the real one.
 */
const reactStubSource = `
let slots = null;
let cursor = 0;
let queued = [];

export const useState = (initial) => {
  const owner = slots;
  if (!owner[cursor]) {
    owner[cursor] = { value: initial };
  }
  const slot = owner[cursor++];
  return [slot.value, (next) => {
    if (Object.is(next, slot.value)) return;
    slot.value = next;
    owner.rerender();
  }];
};

export const useCallback = (fn, deps) => {
  if (!slots[cursor]) {
    slots[cursor] = { fn, deps };
  }
  const slot = slots[cursor++];
  if (deps.some((value, index) => !Object.is(value, slot.deps[index]))) {
    slot.fn = fn;
    slot.deps = deps;
  }
  return slot.fn;
};

export const useEffect = (run, deps) => {
  if (!slots[cursor]) {
    slots[cursor] = { deps: null, ran: false };
  }
  const slot = slots[cursor++];
  const changed = !slot.ran || deps.some((value, index) => !Object.is(value, slot.deps[index]));
  slot.ran = true;
  slot.deps = deps;
  if (changed) {
    queued.push(run);
  }
};

export const createComponent = (body) => {
  const componentSlots = [];
  let last;
  const run = () => {
    slots = componentSlots;
    cursor = 0;
    queued = [];
    last = body();
    const effects = queued;
    queued = [];
    slots = null;
    effects.forEach((effect) => effect());
  };
  componentSlots.rerender = run;
  run();
  return { get value() { return last; } };
};
`;

/** The scan-blocked read, held open until a test answers it. */
const apiStubSource = `
export const control = { calls: 0, settle: null };
export default {
  getCacheScanBlocked: () => {
    control.calls += 1;
    return new Promise((resolve, reject) => {
      control.settle = { resolve, reject };
    });
  }
};
`;

const reactUrl = moduleUrl(reactStubSource);
const apiUrl = moduleUrl(apiStubSource);
const { createComponent } = await import(reactUrl);
const { control } = await import(apiUrl);

const { useCacheScanBlocked } = await import(
  await compileToUrl('../src/hooks/useCacheScanBlocked.ts', {
    react: reactUrl,
    'react-i18next': moduleUrl('export const useTranslation = () => ({ t: (key) => key });'),
    '@services/api.service': apiUrl,
    '@contexts/SignalRContext/useSignalR': moduleUrl(
      'export const useSignalR = () => ({ on: () => {}, off: () => {}, isConnected: true });'
    ),
    '@hooks/useReconnectRefetch': moduleUrl('export const useReconnectRefetch = () => {};'),
    '@utils/error': moduleUrl(
      'export const isAbortError = (error) => error instanceof Error && error.name === "AbortError";'
    )
  })
);

const BLOCKED_KEY = 'management.gameDetection.blockedWhileDownloading';
const CHECKING_KEY = 'management.gameDetection.checkingForDownload';

/** The two sentences `CacheScanGate.CheckDownloadInProgress` can return, verbatim. */
const DOWNLOADING =
  'A client download is writing to the cache right now. Try again once it finishes.';
const TRACKER_SILENT =
  'The download tracker has not reported yet, so a scan cannot tell whether the cache is being written to. Try again in a few seconds.';

const mount = () => {
  control.calls = 0;
  control.settle = null;
  return createComponent(() => useCacheScanBlocked());
};

test('the window before the first answer is neither ready nor blocked', async () => {
  const gate = mount();

  assert.equal(control.calls, 1, 'the mount asks the server');
  assert.equal(gate.value.available, false, 'nothing has said a scan may start');
  assert.equal(gate.value.blocked, false, 'nothing has said a download is running either');
  assert.equal(
    gate.value.tooltip,
    CHECKING_KEY,
    'the hover has to say what is actually true, which is that the answer is still on the wire'
  );
});

test('a blocked answer carries the sentence the server sent, not a client one', async () => {
  const gate = mount();

  control.settle.resolve({ blocked: true, reason: DOWNLOADING });
  await tick();

  assert.equal(gate.value.available, false);
  assert.equal(gate.value.blocked, true);
  assert.equal(gate.value.tooltip, DOWNLOADING);
});

test('the tracker having no answer yet is not reported as a download', async () => {
  const gate = mount();

  control.settle.resolve({ blocked: true, reason: TRACKER_SILENT });
  await tick();

  assert.equal(gate.value.blocked, true, 'the server still refuses a scan');
  assert.equal(
    gate.value.tooltip,
    TRACKER_SILENT,
    'both causes arrive as blocked and only the server knows which, so its sentence is the one shown'
  );
});

test('a clear answer offers the control with no explanation attached', async () => {
  const gate = mount();

  control.settle.resolve({ blocked: false });
  await tick();

  assert.equal(gate.value.available, true);
  assert.equal(gate.value.blocked, false);
  assert.equal(gate.value.tooltip, '', 'an offered control gains no tooltip');
});

test('a failed read opens the gate rather than holding it shut on an unestablished reason', async () => {
  const gate = mount();

  control.settle.reject(new Error('network down'));
  await tick();

  assert.equal(
    gate.value.available,
    true,
    'the server still refuses the click with its own reason'
  );
  assert.equal(gate.value.blocked, false, 'a read that failed proves nothing about a download');
  assert.equal(gate.value.tooltip, '');
});

const FILES = [
  'src/components/features/management/cache/CacheManager.tsx',
  'src/components/features/management/cache/CorruptionManager.tsx',
  'src/components/features/management/game-detection/GameCacheDetector.tsx',
  'src/components/features/management/sections/StorageSection.tsx'
];

/** Every `DiskObjectActionGate` in a component, as the source text of its two decisive props. */
const gatesIn = (file) => {
  const sourceFile = parseSource(file, typescript.ScriptKind.TSX);
  const opened = collectNodes(
    sourceFile,
    (node) =>
      typescript.isJsxOpeningElement(node) &&
      node.tagName.getText(sourceFile) === 'DiskObjectActionGate'
  );
  return opened.map((element) => {
    const propText = (name) => {
      const attribute = element.attributes.properties.find(
        (property) =>
          typescript.isJsxAttribute(property) && property.name.getText(sourceFile) === name
      );
      assert.ok(attribute, `${file}: a scan gate with no ${name}`);
      return attribute.initializer.expression.getText(sourceFile);
    };
    return { file, available: propText('available'), tooltip: propText('tooltip') };
  });
};

test('all five scan controls ask the same question and show the same answer', () => {
  // The same wrapper carries the unrelated disk-objects capability gate on removal controls, so
  // the scan controls are the ones whose availability is decided by this hook.
  const gates = FILES.flatMap(gatesIn).filter((gate) => gate.available.includes('scanGate'));

  assert.equal(gates.length, 5, 'the five controls the server refuses');

  for (const gate of gates) {
    assert.ok(
      gate.available.includes('scanGate.available'),
      `${gate.file}: a control offered on anything but a confirmed clear answer`
    );
    assert.ok(
      gate.tooltip.includes('scanGate.tooltip'),
      `${gate.file}: a hover that picks its own words instead of the gate's`
    );
    assert.ok(
      !gate.tooltip.includes(BLOCKED_KEY),
      `${gate.file}: the download sentence is shown before a download has been established`
    );
  }
});

test('only the cache card reads the blocked answer, and only to say a download is running', () => {
  const uses = FILES.filter((file) =>
    readFileSync(new URL(`../${file}`, import.meta.url), 'utf8').includes('scanGate.blocked')
  );

  assert.deepEqual(
    uses,
    [FILES[0]],
    'deciding whether to offer a control reads available, not blocked'
  );

  const sourceFile = parseSource(FILES[0], typescript.ScriptKind.TSX);
  const condition = findSoleNode(
    sourceFile,
    'the cache card notice condition',
    (node) =>
      typescript.isBinaryExpression(node) &&
      node.operatorToken.kind === typescript.SyntaxKind.BarBarToken &&
      node.left.getText(sourceFile) === 'cacheSizeDenialReason'
  );

  assert.equal(
    condition.right.getText(sourceFile),
    'scanGate.blocked',
    'the yellow notice states a download is in progress, so it needs the server to have said so'
  );
});

test('the checking sentences are translated in both shipped locales', () => {
  for (const locale of ['en', 'zh']) {
    const strings = JSON.parse(
      readFileSync(new URL(`../src/i18n/locales/${locale}.json`, import.meta.url), 'utf8')
    );
    for (const key of ['checkingForDownload', 'checkingProcessedLogs']) {
      const text = strings.management.gameDetection[key];
      assert.equal(typeof text, 'string', `${locale}.json has no ${key}`);
      assert.ok(text.length > 0, `${locale}.json has an empty ${key}`);
    }
  }
});

const DETECTOR = FILES[2];

/** The component's own `noProcessedLogs`, run against a setup-status the context could hold. */
const noProcessedLogs = (state) => {
  const sourceFile = parseSource(DETECTOR, typescript.ScriptKind.TSX);
  const declared = findSoleNode(
    sourceFile,
    'the noProcessedLogs declaration',
    (node) =>
      typescript.isVariableDeclaration(node) && node.name.getText(sourceFile) === 'noProcessedLogs'
  );
  return bindLifted(`() => (${declared.initializer.getText(sourceFile)})`, state)();
};

/** The empty state's subtitle, for the same states, with `t` handing back the key. */
const emptyStateSubtitle = (state) => {
  const sourceFile = parseSource(DETECTOR, typescript.ScriptKind.TSX);
  const conditional = findSoleNode(
    sourceFile,
    'the empty-state subtitle',
    (node) =>
      typescript.isConditionalExpression(node) &&
      node.whenTrue.getText(sourceFile).includes('processLogsFirst')
  );
  return bindLifted(`() => (${conditional.getText(sourceFile)})`, {
    noProcessedLogs: noProcessedLogs(state),
    t: (key) => key
  })();
};

const PROCESS_FIRST = 'management.gameDetection.emptyState.processLogsFirst';
const CLICK_SCAN = 'management.gameDetection.emptyState.clickFullScan';

/** The placeholder the context writes when the status call fails, in the fields read here. */
const UNREAD = { hasProcessedLogs: false };

test('the empty state only says to process logs first when the server said there are none', () => {
  assert.equal(
    emptyStateSubtitle({ isSetupStatusKnown: true, setupStatus: UNREAD }),
    PROCESS_FIRST,
    'the server answered that no logs are processed, which is the one case that sentence is true'
  );

  assert.equal(
    emptyStateSubtitle({ isSetupStatusKnown: false, setupStatus: null }),
    CLICK_SCAN,
    'nothing has answered yet, so nothing can be claimed about the logs'
  );

  assert.equal(
    emptyStateSubtitle({ isSetupStatusKnown: false, setupStatus: UNREAD }),
    CLICK_SCAN,
    'the status call failed and left its placeholder, which is not the server saying anything'
  );

  assert.equal(
    emptyStateSubtitle({ isSetupStatusKnown: true, setupStatus: { hasProcessedLogs: true } }),
    CLICK_SCAN
  );
});

test('the game cache scans wait for the setup status, and do not wait for an answer that failed', () => {
  const gates = gatesIn(DETECTOR).filter((gate) => gate.available.includes('scanGate'));
  assert.equal(gates.length, 2, 'Quick Scan and Full Scan');

  for (const gate of gates) {
    const offered = (setupStatusLoading) =>
      bindLifted(`() => (${gate.available})`, {
        scanGate: { available: true },
        setupStatusLoading
      })();

    assert.equal(offered(true), false, 'the ask is outstanding, so the scan is not offered yet');
    assert.equal(
      offered(false),
      true,
      'the ask settled, and a settle with no answer must not hold the scan shut for good'
    );

    const hover = (scanGate) =>
      bindLifted(`() => (${gate.tooltip})`, { scanGate, t: (key) => key })();

    assert.equal(
      hover({ available: true, tooltip: '' }),
      'management.gameDetection.checkingProcessedLogs',
      'the setup status is the outstanding one, so the hover names it'
    );
    assert.equal(
      hover({ available: false, tooltip: CHECKING_KEY }),
      CHECKING_KEY,
      'the scan gate is the outstanding one, so its own sentence wins'
    );
  }
});
