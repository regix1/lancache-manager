import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

/**
 * Picking a new time range keeps the previous window's figures up until the batch lands, so the
 * click itself must not repaint anything: a slice written at fetch start shows one value for the
 * length of the request and is then replaced by the response, which reads as a flicker. The only
 * write the fetch may make before it has an answer is the skeleton flag, and the range-change
 * caller leaves that off.
 */

const source = readFileSync(
  new URL('../src/contexts/DashboardDataContext/index.tsx', import.meta.url),
  'utf8'
);
const file = ts.createSourceFile(
  'index.tsx',
  source,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TSX
);

const collectIn = (root, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) {
      found.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(root, visit);
  return found;
};

const stateSetters = new Set(
  collectIn(
    file,
    (node) =>
      ts.isVariableDeclaration(node) &&
      ts.isArrayBindingPattern(node.name) &&
      node.initializer !== undefined &&
      ts.isCallExpression(node.initializer) &&
      node.initializer.expression.getText(file) === 'useState'
  ).map((node) => node.name.elements[1].name.getText(file))
);

const [fetchAllData] = collectIn(
  file,
  (node) => ts.isVariableDeclaration(node) && node.name.getText(file) === 'fetchAllData'
);

test('the fetch writes no slice before the batch answers', () => {
  assert.ok(fetchAllData, 'fetchAllData is where every dashboard request starts');
  assert.ok(stateSetters.size > 0, 'the provider holds its slices in useState');

  const body = fetchAllData.initializer.arguments[0];
  const [request] = collectIn(
    body,
    (node) =>
      ts.isCallExpression(node) && node.expression.getText(file) === 'ApiService.getDashboardBatch'
  );
  assert.ok(request, 'the batch endpoint is the request every slice waits on');

  const requestStart = request.getStart(file);
  const earlyWrites = collectIn(
    body,
    (node) =>
      ts.isCallExpression(node) &&
      ts.isIdentifier(node.expression) &&
      stateSetters.has(node.expression.text) &&
      node.end <= requestStart
  ).map((node) => node.expression.text);

  assert.deepEqual(
    [...new Set(earlyWrites)].sort(),
    ['setLoading'],
    'a slice written here paints a value the response immediately replaces'
  );
});

test('a range change asks for no skeleton', () => {
  const [rangeChange] = collectIn(
    file,
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(file) === 'fetchAllData' &&
      node.arguments.length === 1 &&
      node.arguments[0].getText(file).includes('timeRangeChange')
  );
  assert.ok(rangeChange, 'the time range effect is what refetches on a new range');

  const showLoading = rangeChange.arguments[0].properties.find(
    (property) => property.name.getText(file) === 'showLoading'
  );
  assert.equal(
    showLoading?.initializer.getText(file),
    'false',
    'a skeleton here hides the old figures without replacing them until the fetch completes'
  );
});
