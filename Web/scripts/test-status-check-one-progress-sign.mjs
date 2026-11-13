import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import { compileToUrl, moduleUrl, translator } from './transpile-module.mjs';

/**
 * While a Status Check runs, the Run Check button keeps its busy spinner and the domain bar counts
 * the domains it has checked. The "Did recent downloads use the cache?" block must not add a
 * spinner of its own: one sign of progress per run. It shows waiting text, or the last result
 * until the new one replaces it.
 */

const SUMMARY = '../src/components/features/management/status-check/ContentPathSummary.tsx';
const SPINNER_MARK = 'data-loading-spinner';

globalThis.React = React;
const english = translator('en');
globalThis.contentSummaryTranslate = (key, vars) => {
  try {
    return english(key, vars);
  } catch {
    return english(`${key}_other`, vars);
  }
};

// Every stylesheet the component imports is empty here; node cannot load CSS.
const source = await readFile(new URL(SUMMARY, import.meta.url), 'utf8');
const stylesheets = Object.fromEntries(
  [...source.matchAll(/import\s+'([^']+\.css)'/g)].map((match) => [match[1], moduleUrl('')])
);

const { default: ContentPathSummary } = await import(
  await compileToUrl(
    SUMMARY,
    {
      ...stylesheets,
      react: import.meta.resolve('react'),
      'react-i18next': moduleUrl(
        'export const useTranslation = () => ({ t: globalThis.contentSummaryTranslate });'
      ),
      '@components/common/LoadingSpinner': moduleUrl(
        `export default () => globalThis.React.createElement('span', { '${SPINNER_MARK}': 'true' });`
      ),
      '@components/ui/HelpPopover': moduleUrl(
        [
          'const pass = (props) => globalThis.React.createElement("div", null, props.children ?? null);',
          'export const HelpPopover = pass, HelpSection = pass, HelpNote = pass;'
        ].join('\n')
      ),
      '@utils/formatters': moduleUrl('export const formatBytes = (bytes) => `${bytes} B`;'),
      '@hooks/useFormattedDateTime': moduleUrl(
        'export const useFormattedDateTime = () => "Nov 11, 2026, 6:34 PM";'
      ),
      './contentPathHelpers': await compileToUrl(
        '../src/components/features/management/status-check/contentPathHelpers.ts'
      )
    },
    { jsx: typescript.JsxEmit.React }
  )
);

/** The last finished check's recent-downloads answer, kept on screen while the next one runs. */
const lastReport = {
  availability: 'noSamples',
  checkedAtUtc: '2026-10-28T21:33:00Z',
  scannedBytes: 1048576,
  scanTruncated: false,
  paths: []
};

test('a running check shows no second spinner in the recent downloads block', () => {
  for (const [label, report] of [
    ['with no earlier result', null],
    ['with the last result on screen', lastReport]
  ]) {
    const markup = renderToStaticMarkup(
      React.createElement(ContentPathSummary, { report, isRunning: true })
    );

    assert.ok(
      !markup.includes(SPINNER_MARK),
      `"Did recent downloads use the cache?" shows its own spinner while the check runs (${label}), beside the Run Check button's: ${markup}`
    );
  }
});
