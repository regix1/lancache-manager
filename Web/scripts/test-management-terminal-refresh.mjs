import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

const managerPath = new URL(
  '../src/components/features/management/cache/CorruptionManager.tsx',
  import.meta.url
);
const managerSource = readFileSync(managerPath, 'utf8');
const syntaxTree = ts.createSourceFile(
  managerPath.pathname,
  managerSource,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TSX
);

let removalHandler;
const visit = (node) => {
  if (
    ts.isVariableDeclaration(node) &&
    ts.isIdentifier(node.name) &&
    node.name.text === 'handleRemovalComplete'
  ) {
    removalHandler = node.initializer;
  }
  ts.forEachChild(node, visit);
};
visit(syntaxTree);

assert.ok(removalHandler && ts.isArrowFunction(removalHandler), 'removal callback was not found');

const guardName = ['result', 'E', 'poch', 'Ref'].join('');
const compiled = ts.transpileModule(
  `
    export const makeHandler = (scope) => {
      const {
        setHistoryRefreshKey,
        detectionMethod,
        ${guardName},
        ApiService,
        applyCachedScan,
        setCachedLoadError,
        notifyError,
        beginLoad,
        markLoaded,
        t
      } = scope;
      return ${removalHandler.getText(syntaxTree)};
    };
  `,
  {
    compilerOptions: {
      target: ts.ScriptTarget.ES2022,
      module: ts.ModuleKind.ES2022
    }
  }
);
const moduleUrl = `data:text/javascript;base64,${Buffer.from(compiled.outputText).toString('base64')}`;
const { makeHandler } = await import(moduleUrl);

const settled = () => new Promise((resolve) => setImmediate(resolve));

const createScope = () => {
  const calls = {
    history: 0,
    loads: [],
    applied: [],
    errors: [],
    notices: [],
    started: 0,
    finished: 0
  };
  const guard = { current: 1 };
  const scan = { hasCachedResults: true, detectionMethod: 'structural', scanId: 'scan-1' };
  const scope = {
    setHistoryRefreshKey: (update) => {
      calls.history = update(calls.history);
    },
    detectionMethod: 'structural',
    [guardName]: guard,
    ApiService: {
      getCachedCorruptionDetection: async (method) => {
        calls.loads.push(method);
        return scan;
      }
    },
    applyCachedScan: (result, method) => {
      calls.applied.push({ result, method });
    },
    setCachedLoadError: (error) => calls.errors.push(error),
    notifyError: (...args) => calls.notices.push(args),
    beginLoad: () => {
      calls.started += 1;
    },
    markLoaded: () => {
      calls.finished += 1;
    },
    t: (key) => key
  };
  return { calls, guard, scan, scope };
};

for (const terminal of [
  { label: 'success', success: true },
  { label: 'cancellation', success: false, cancelled: true },
  { label: 'failure', success: false, cancelled: false, error: 'remove failed' }
]) {
  test(`${terminal.label} reloads the selected corruption evidence`, async () => {
    const { calls, scan, scope } = createScope();
    const handler = makeHandler(scope);

    handler({
      ...terminal,
      service: 'steam',
      detectionMethod: 'structural'
    });
    await settled();

    assert.equal(calls.history, 1);
    assert.deepEqual(calls.loads, ['structural']);
    assert.deepEqual(calls.applied, [{ result: scan, method: 'structural' }]);
    assert.equal(calls.started, 1);
    assert.equal(calls.finished, 1);
  });
}

test('another detection method refreshes history without replacing the selection', async () => {
  const { calls, scope } = createScope();
  const handler = makeHandler(scope);

  handler({
    success: false,
    cancelled: true,
    service: 'steam',
    detectionMethod: 'repeated_miss'
  });
  await settled();

  assert.equal(calls.history, 1);
  assert.deepEqual(calls.loads, []);
  assert.deepEqual(calls.applied, []);
});

test('a late removal reload cannot replace a newer selection', async () => {
  const { calls, guard, scan, scope } = createScope();
  let completeRequest;
  scope.ApiService.getCachedCorruptionDetection = () =>
    new Promise((resolve) => {
      completeRequest = resolve;
    });
  const handler = makeHandler(scope);

  handler({
    success: true,
    service: 'steam',
    detectionMethod: 'structural'
  });
  guard.current += 1;
  completeRequest(scan);
  await settled();

  assert.deepEqual(calls.applied, []);
  assert.deepEqual(calls.errors, []);
  assert.equal(calls.finished, 1);
});
