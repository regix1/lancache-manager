import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

/**
 * The five copy buttons used to clear their "Copied" state with a bare `setTimeout`, so copying
 * twice inside the window let the first timer fire and cut the second confirmation short. All
 * five now go through `useCopyFeedback`, which reschedules on every call instead of letting an
 * earlier timer win. This proves both halves: the five sites actually adopted it (source shape),
 * and the hook it is built on actually reschedules rather than merely restating that it does
 * (behavior, replayed from the real hook source with a stubbed React).
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const COPY_SITES = [
  'src/components/features/management/grafana/GrafanaEndpoints.tsx',
  'src/components/features/management/status-check/ClientProbeCard.tsx',
  'src/components/features/management/theme/ThemeEditorForm.tsx',
  'src/components/modals/auth/ApiKeyRotatedModal.tsx',
  'src/components/modals/auth/XboxAuthModal.tsx'
];

test('none of the five copy sites schedules its own bare timer any more', () => {
  for (const relativePath of COPY_SITES) {
    const source = readWebSource(relativePath);
    const sourceFile = ts.createSourceFile(
      relativePath,
      source,
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.TSX
    );
    const bareTimeouts = [];
    const visit = (node) => {
      if (
        ts.isCallExpression(node) &&
        ts.isIdentifier(node.expression) &&
        node.expression.text === 'setTimeout'
      ) {
        bareTimeouts.push(node.getText(sourceFile));
      }
      ts.forEachChild(node, visit);
    };
    visit(sourceFile);
    assert.deepEqual(bareTimeouts, [], `${relativePath} should hold no bare setTimeout call`);
  }
});

test('each of the five copy sites imports useCopyFeedback', () => {
  for (const relativePath of COPY_SITES) {
    const source = readWebSource(relativePath);
    assert.match(
      source,
      /import\s*\{[^}]*\buseCopyFeedback\b[^}]*\}\s*from\s*['"](?:@\/hooks|@hooks)\/useCopyFeedback['"]/,
      `${relativePath} should import useCopyFeedback`
    );
  }
});

/**
 * Compiles a hook's TypeScript source to a CommonJS module and runs it against a minimal stand-in
 * for React - enough to call the hook body once and keep calling the stable closures it returns,
 * which is all a single "mount" needs since none of these hooks re-render on their own.
 *
 * @param {string} relativePath hook source, relative to this scripts directory
 * @param {Record<string, unknown>} fakeModules module specifier -> the object `require` resolves to
 * @returns {Record<string, unknown>} the compiled module's exports
 */
function loadHook(relativePath, fakeModules) {
  const source = readFileSync(new URL(relativePath, import.meta.url), 'utf8');
  const compiled = transpile(source, ts.ModuleKind.CommonJS);
  const moduleObject = { exports: {} };
  const fakeRequire = (specifier) => {
    if (specifier in fakeModules) return fakeModules[specifier];
    throw new Error(`no stub registered for require('${specifier}') while loading ${relativePath}`);
  };
  new Function('module', 'exports', 'require', compiled)(
    moduleObject,
    moduleObject.exports,
    fakeRequire
  );
  return moduleObject.exports;
}

/** A `useRef`/`useCallback`/`useEffect` stub good for one hook call: the returned closures stay
 *  stable exactly as `useCallback` promises, so calling them repeatedly afterwards is faithful. */
const reactRefCallbackEffect = {
  useRef: (initial) => ({ current: initial }),
  useCallback: (fn) => fn,
  useEffect: (fn) => fn()
};

/** A `useState` stub that hands the test a live box instead of a point-in-time snapshot, since
 *  nothing here re-renders to obtain a fresh one - the real component's re-render is what a
 *  browser measurement covers; this proves the timer semantics underneath it. */
function fakeUseState(initial) {
  const box = { current: initial };
  const useState = () => [box.current, (value) => (box.current = value)];
  return { useState, box };
}

const { useTimeoutCallback } = loadHook('../src/hooks/useTimeoutCallback.ts', {
  react: reactRefCallbackEffect
});

test('useTimeoutCallback: a second schedule cancels the first, so only the second delay counts', async () => {
  const schedule = useTimeoutCallback(60);
  let firings = 0;
  schedule(() => firings++);
  await sleep(30); // well inside the 60ms window
  schedule(() => firings++); // reschedule - must cancel the pending one above
  await sleep(45); // 75ms since the FIRST call, but only 45ms since the second: not due yet
  assert.equal(firings, 0, 'the first schedule should have been cancelled, not fired early');
  await sleep(30); // now 75ms since the second call, past its 60ms delay
  assert.equal(firings, 1, 'exactly the second schedule should fire, exactly once');
});

test('useCopyFeedback: copying twice inside the window keeps the confirmation for a fresh window after the second copy', async () => {
  const { useState, box } = fakeUseState(false);
  const { useCopyFeedback } = loadHook('../src/hooks/useCopyFeedback.ts', {
    react: { useState, useCallback: reactRefCallbackEffect.useCallback },
    './useTimeoutCallback': { useTimeoutCallback }
  });

  const [, markCopied] = useCopyFeedback(false, 60);

  markCopied(true);
  await sleep(30); // less than 60ms since the first copy
  assert.equal(box.current, true, 'the first copy should still be showing');

  markCopied(true); // a second click inside the window - the confirmation must not cut short
  await sleep(45); // 75ms since the FIRST click, only 45ms since the second
  assert.equal(
    box.current,
    true,
    'the second click reschedules the clear - it must not have been cut off by the first timer'
  );

  await sleep(30); // now past 60ms since the second click
  assert.equal(box.current, false, 'the confirmation clears a full window after the LAST click');
});
