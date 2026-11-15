import assert from 'node:assert/strict';
import path from 'node:path';
import test from 'node:test';
import ts from 'typescript';
import {
  collectNodes,
  location,
  normalizePath,
  parseSource,
  repository,
  sourceFiles
} from './source-contracts.mjs';

/**
 * The full-screen setup gate (`SetupGate`) and the access wizard drawn on it (`AccessSetup`) fade out
 * only when their host keeps them drawn and closes them with `opened={false}`. A host that draws one
 * without `opened` can only take it away by unmounting it, so the screen vanishes in one frame when
 * its task succeeds. Every place in the app that draws either one passes `opened`. The places are
 * found by reading every shipped `.tsx` file.
 */

const GATES = new Set(['SetupGate', 'AccessSetup']);

/** Each JSX element drawing a gate, with where it is and whether it passes `opened`. */
const gateElements = ({ file, text }) => {
  const source = parseSource(normalizePath(path.relative(repository, file)), text);
  const isGateElement = (node) =>
    (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) &&
    GATES.has(node.tagName.getText(source));
  return collectNodes(source, isGateElement).map((node) => ({
    where: location(source, node),
    gate: node.tagName.getText(source),
    passesOpened: node.attributes.properties.some(
      (attribute) => ts.isJsxAttribute(attribute) && attribute.name.getText(source) === 'opened'
    )
  }));
};

test('every place that draws the setup gate or the access wizard passes opened', () => {
  const drawn = sourceFiles()
    .filter(({ file }) => file.endsWith('.tsx'))
    .flatMap(gateElements);
  assert.ok(
    drawn.length > 0,
    'no shipped file draws SetupGate or AccessSetup, so the test proves nothing'
  );
  const unfaded = drawn
    .filter((element) => !element.passesOpened)
    .map((element) => `${element.where} draws <${element.gate}> with no opened`);
  assert.deepEqual(
    unfaded,
    [],
    `these gates can only vanish by unmounting, with no fade: ${unfaded.join('; ')}`
  );
});
