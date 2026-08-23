import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import ts from 'typescript';

/**
 * A control that is invisible until the pointer arrives (`opacity-0` at rest, revealed on hover)
 * is unreachable by keyboard unless something ALSO reveals it on focus. This walks every
 * `<button>`/`<a>`/`<Button>` under `src/components/`, and for each one whose own class list or an
 * ancestor's carries a hidden-at-rest `opacity-0`, asserts the same chain also carries a focus
 * override: `focus-visible:opacity-100` on the control itself, or `focus-within:opacity-100`
 * (bare or `group-`/breakpoint-prefixed) somewhere up the tree.
 */

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC_ROOT = resolve(__dirname, '../src');
const COMPONENTS_ROOT = join(SRC_ROOT, 'components');

/** @returns {string[]} every .tsx file under dir */
function listTsxFiles(dir, out = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) listTsxFiles(full, out);
    else if (entry.name.endsWith('.tsx')) out.push(full);
  }
  return out;
}

/** Raw source text of a `className={...}` value, or the literal string. `null` when unset. */
function classAttributeText(attributes, sourceFile) {
  for (const attribute of attributes.properties) {
    if (!ts.isJsxAttribute(attribute) || attribute.name.getText(sourceFile) !== 'className') {
      continue;
    }
    const init = attribute.initializer;
    if (!init) return null;
    if (ts.isStringLiteral(init)) return init.text;
    if (ts.isJsxExpression(init) && init.expression) return init.expression.getText(sourceFile);
  }
  return null;
}

/** A class token that hides the element by default: bare `opacity-0`, or breakpoint-gated. */
const HIDDEN_AT_REST = /(?<![\w:-])(?:(?:sm|md|lg|xl|2xl):)?opacity-0\b/;

/**
 * The companion that actually makes it a HOVER reveal, as opposed to e.g. a mount/unmount
 * animation driven by component state. Without this a `Modal.tsx`-style `isAnimating` fade
 * (also textually `opacity-0` at rest) would be flagged for a defect it does not have.
 */
const HOVER_REVEAL = /(?<![\w:-])(?:(?:sm|md|lg|xl|2xl):)?(?:group-)?hover:opacity-100\b/;

/** Either shape the codebase uses to bring a hidden-at-rest control back for keyboard focus. */
function hasFocusOverride(classChain) {
  return classChain.some(
    (text) =>
      text.includes('focus-visible:opacity-100') || text.includes('focus-within:opacity-100')
  );
}

/** className text of `node` plus every enclosing JSX element, self first. */
function classChainOf(node, sourceFile, selfClassText) {
  const chain = selfClassText ? [selfClassText] : [];
  for (let current = node.parent; current; current = current.parent) {
    if (!ts.isJsxElement(current)) continue;
    const text = classAttributeText(current.openingElement.attributes, sourceFile);
    if (text) chain.push(text);
  }
  return chain;
}

/** @returns {{ tag: string, selfClass: string | null, chain: string[], line: number }[]} */
function findRevealCandidates(filePath) {
  const source = readFileSync(filePath, 'utf8');
  const sourceFile = ts.createSourceFile(
    filePath,
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX
  );
  const found = [];

  const visit = (node) => {
    let tag = null;
    let attributes = null;
    if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) {
      tag = node.tagName.getText(sourceFile);
      attributes = node.attributes;
    }
    // `Button` is the shared wrapper (ui/Button.tsx); it renders a native <button>, so a
    // hidden-at-rest control written with it needs the same focus reveal as a raw one.
    if (tag === 'button' || tag === 'a' || tag === 'Button') {
      const selfClass = classAttributeText(attributes, sourceFile);
      const chain = classChainOf(node, sourceFile, selfClass);
      const isHoverReveal =
        chain.some((text) => HIDDEN_AT_REST.test(text)) &&
        chain.some((text) => HOVER_REVEAL.test(text));
      if (isHoverReveal) {
        const { line } = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile));
        found.push({ tag, selfClass, chain, line: line + 1 });
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(sourceFile);
  return found.map((candidate) => ({ ...candidate, filePath }));
}

const allCandidates = listTsxFiles(COMPONENTS_ROOT).flatMap(findRevealCandidates);

test('every hover-revealed button or link also reveals on keyboard focus', () => {
  const unprotected = allCandidates.filter((candidate) => !hasFocusOverride(candidate.chain));
  assert.deepEqual(
    unprotected.map((c) => `${c.filePath}:${c.line}`),
    [],
    'a control that is invisible until hovered must also become visible on focus-visible or a focus-within ancestor'
  );
});

test('the sweep actually found hidden-at-rest controls to check, and did not vacuously pass', () => {
  assert.ok(
    allCandidates.length > 0,
    'expected at least one opacity-0 button/link under src/components - the matcher may be broken'
  );
});

test('the dashboard hide-card button keeps its focus-visible reveal', () => {
  const dashboard = allCandidates.filter((c) => c.filePath.endsWith('Dashboard.tsx'));
  assert.ok(dashboard.length > 0, 'expected Dashboard.tsx to contain a hidden-at-rest button');
  for (const candidate of dashboard) {
    assert.ok(
      candidate.chain.some((text) => text.includes('focus-visible:opacity-100')),
      'Dashboard.tsx hide-card button should carry focus-visible:opacity-100 on itself'
    );
  }
});
