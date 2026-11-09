import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import { findSoleNode, parseSource } from './transpile-module.mjs';

/**
 * The game card's badge row sits in the title column beside the Remove button. On a phone the
 * service, App ID and slice badges together are wider than that column, so the row has to wrap
 * inside it; a row that cannot shrink runs under the button.
 */

const gameCard = 'src/components/features/management/game-detection/GameCard.tsx';

const badgeRowClasses = () => {
  const sourceFile = parseSource(gameCard, typescript.ScriptKind.TSX);
  const subtitle = findSoleNode(
    sourceFile,
    'subtitle declaration',
    (node) => typescript.isVariableDeclaration(node) && node.name.getText(sourceFile) === 'subtitle'
  );
  const row = findSoleNode(
    sourceFile,
    'badge row element',
    (node) =>
      typescript.isJsxOpeningElement(node) && node.parent === subtitle.initializer.expression
  );
  const className = row.attributes.properties.find(
    (attribute) => attribute.name.getText(sourceFile) === 'className'
  );
  return className.initializer.text.split(/\s+/);
};

test('the badge row wraps inside the title column', () => {
  const classes = badgeRowClasses();
  assert.ok(classes.includes('flex-wrap'), `badge row classes: ${classes.join(' ')}`);
  assert.ok(!classes.includes('flex-shrink-0'), `badge row classes: ${classes.join(' ')}`);
});
