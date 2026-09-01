import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

/**
 * The client-IP session group is rendered twice: once in the expanded card and once in the drawer.
 * They used to be two hand-maintained copies of the same 200 lines, and the drawer copy had drifted -
 * it dropped the dimmed row and both evicted badges, so one download read as evicted in the card and
 * as normal in the drawer.
 *
 * These checks pin the shape that makes that impossible: the markup lives in one component, the
 * evicted treatment appears once inside it, and NormalView keeps no second copy to drift again.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const parse = (fileName, relativePath) =>
  ts.createSourceFile(
    fileName,
    readWebSource(relativePath),
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX
  );

const groupFile = parse(
  'IpDownloadGroup.tsx',
  'src/components/features/downloads/IpDownloadGroup.tsx'
);
const normalViewFile = parse('NormalView.tsx', 'src/components/features/downloads/NormalView.tsx');

const collect = (sourceFile, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) {
      found.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);
  return found;
};

const tagNameOf = (node) => {
  if (ts.isJsxSelfClosingElement(node)) return node.tagName.getText(node.getSourceFile());
  if (ts.isJsxElement(node)) return node.openingElement.tagName.getText(node.getSourceFile());
  return null;
};

const elementsNamed = (sourceFile, tagName) =>
  collect(sourceFile, (node) => tagNameOf(node) === tagName);

const classNameOf = (node, sourceFile) => {
  const opening = ts.isJsxSelfClosingElement(node) ? node : node.openingElement;
  const attribute = opening.attributes.properties.find(
    (property) => ts.isJsxAttribute(property) && property.name.getText(sourceFile) === 'className'
  );
  return attribute?.initializer ? attribute.initializer.getText(sourceFile) : '';
};

/** The nearest enclosing text that guards this node, so `a && <X />` can be told from a bare `<X />`. */
const guardOf = (node, sourceFile) => {
  for (let current = node.parent; current; current = current.parent) {
    if (
      ts.isBinaryExpression(current) &&
      current.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken
    ) {
      return current.left.getText(sourceFile);
    }
  }
  return null;
};

test('the session row carries the dimmed evicted treatment, in one place', () => {
  const rows = collect(
    groupFile,
    (node) =>
      tagNameOf(node) !== null && classNameOf(node, groupFile).includes('drawer-session-row')
  );

  assert.equal(rows.length, 1, 'expected exactly one session row element in IpDownloadGroup.tsx');

  const className = classNameOf(rows[0], groupFile);
  assert.match(
    className,
    /download\.isEvicted/,
    'the session row className must branch on download.isEvicted'
  );
  assert.match(className, /opacity-60/, 'an evicted session row must be dimmed with opacity-60');
});

test('both evicted badges are guarded and live in the shared component', () => {
  const badges = elementsNamed(groupFile, 'EvictedBadge');

  assert.equal(
    badges.length,
    2,
    'expected the mobile and the desktop badge slot to render EvictedBadge'
  );

  for (const badge of badges) {
    assert.equal(
      guardOf(badge, groupFile),
      'download.isEvicted',
      'EvictedBadge must render only when the download is evicted'
    );
  }
});

test('NormalView renders the shared group twice and keeps no copy of its markup', () => {
  assert.equal(
    elementsNamed(normalViewFile, 'IpDownloadGroup').length,
    2,
    'the card view and the drawer must both render IpDownloadGroup'
  );

  assert.equal(
    elementsNamed(normalViewFile, 'EvictedBadge').length,
    0,
    'NormalView must not re-declare the session row markup'
  );

  const rows = collect(
    normalViewFile,
    (node) =>
      tagNameOf(node) !== null && classNameOf(node, normalViewFile).includes('drawer-session-row')
  );
  assert.equal(rows.length, 0, 'NormalView must not re-declare the session row markup');
});
