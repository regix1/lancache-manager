import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import { compileToUrl, moduleUrl, transpile } from './transpile-module.mjs';

/**
 * Status Check marks a name "wrong IP" or "no answer" when the user's DNS server runs a domain
 * list older than the one the check fetched. A failing result that carries `addedToListAtUtc`
 * (the date the fetched list gained that name) says so: the row shows the dated fact once, and
 * the service shows one summary note with the instruction, never one per row.
 */

const FOLDER = '../src/components/features/management/status-check/';
const NOTE_KEY = 'management.sections.statusCheck.addedToListNote';
const SERVICE_KEY = 'management.sections.statusCheck.addedToListService';
const NOTE_CLASS = 'status-check-domain-note';

globalThis.React = React;
const translations = [];
globalThis.addedNoteTranslate = (key, vars = {}) => {
  translations.push({ key, vars });
  return key;
};

/** What the shared timestamp formatter turns a stored time into, so the test can name it. */
const formatted = (value) => `DATE<${value}>`;
globalThis.addedNoteFormat = formatted;

const pass = (name) =>
  `export const ${name} = (props) => globalThis.React.createElement('div', null, props.badge ?? null, props.children ?? null);`;
const stubs = {
  react: import.meta.resolve('react'),
  'react-i18next': moduleUrl(
    'export const useTranslation = () => ({ t: globalThis.addedNoteTranslate });'
  ),
  '@components/ui/Tooltip': moduleUrl(pass('Tooltip')),
  '@components/ui/HelpPopover': moduleUrl(pass('HelpPopover')),
  '@components/ui/AccordionSection': moduleUrl(pass('AccordionSection')),
  '@components/ui/SectionHeaderActions': moduleUrl(pass('SectionHeaderChip')),
  '@contexts/AccordionGroupContext': moduleUrl(
    'export const useAccordionGroupItem = () => undefined;'
  ),
  '@utils/stageKeyMessage': moduleUrl(
    'export const translateStageKeyMessage = (key) => String(key);'
  ),
  '@utils/serviceColors': moduleUrl(
    [
      'export const UNKNOWN_COLOR_VAR = "--unknown";',
      'export const getServiceColorVar = () => "--unknown";'
    ].join('\n')
  ),
  '@utils/serviceDisplayName': moduleUrl('export const formatServiceLabel = (service) => service;'),
  '@utils/dateTimeFormat': moduleUrl(
    'export const formatTimestamp = (value) => globalThis.addedNoteFormat(value);'
  ),
  '@hooks/useReaderClock': moduleUrl('export const useReaderClock = () => ({});'),
  './ContentPathGroup': moduleUrl('export default () => null;')
};

/**
 * Compiles one shipped file with every import it makes replaced: a stub from the map above, an
 * already compiled sibling, an empty module for a stylesheet, or a null icon for each lucide name.
 */
const compileWith = async (file, siblings = {}) => {
  // Compiled as the test runs it, so an import that only JSX uses is still there to replace.
  const compiled = transpile(
    await readFile(new URL(FOLDER + file, import.meta.url), 'utf8'),
    typescript.ModuleKind.ESNext,
    { jsx: typescript.JsxEmit.React }
  );
  const aliases = {};
  for (const [, names = '', specifier] of compiled.matchAll(
    /^import\s+(?:([^;']*?)\s+from\s+)?'([^']+)';/gm
  )) {
    if (specifier.endsWith('.css')) aliases[specifier] = moduleUrl('');
    else if (specifier === 'lucide-react') {
      const icons = names
        .replace(/[{}\s]/g, '')
        .split(',')
        .filter(Boolean);
      aliases[specifier] = moduleUrl(
        icons.map((icon) => `export const ${icon} = () => null;`).join('\n')
      );
    } else {
      const replacement = siblings[specifier] ?? stubs[specifier];
      assert.ok(replacement, `${file} imports ${specifier}, which this test has no stand-in for`);
      aliases[specifier] = replacement;
    }
  }
  return compileToUrl(FOLDER + file, aliases, { jsx: typescript.JsxEmit.React });
};

const contentPathHelpers = await compileWith('contentPathHelpers.ts');
const helpers = await compileWith('helpers.ts');
const domainLeafRow = await compileWith('DomainLeafRow.tsx', {
  './contentPathHelpers': contentPathHelpers
});
const { default: ServiceResultsList } = await import(
  await compileWith('ServiceResultsList.tsx', {
    './DomainLeafRow': domainLeafRow,
    './contentPathHelpers': contentPathHelpers,
    './helpers': helpers
  })
);

const domain = (originalEntry, status, overrides = {}) => ({
  domain: originalEntry.replace('*.', 'lancache-check.'),
  originalEntry,
  status,
  resolvedIps: status === 'unresolved' ? [] : status === 'resolved' ? ['10.0.0.5'] : ['23.10.4.8'],
  expectedIps: ['10.0.0.5'],
  latencyMs: 4,
  heartbeatVerified: status === 'resolved',
  servedBy: status === 'resolved' ? 'lancache' : null,
  edgeProbe: null,
  error: status === 'unresolved' ? 'signalr.statusCheck.noARecords' : null,
  ...overrides
});

// Nintendo on the user's server: the older names answer with the cache, the names uklans PR #299
// added on Oct 30 do not, and one older name fails for another reason with no date to prove.
const nintendo = {
  service: 'nintendo',
  description: null,
  status: 'partial',
  totalCount: 4,
  resolvedCount: 1,
  domains: [
    domain('*.cdn.nintendo.net', 'resolved'),
    domain('*.hac.p01.eshop.nintendo.net', 'mismatched', {
      addedToListAtUtc: '2026-10-30T18:22:41Z'
    }),
    domain('*.cdn.c.shop.nintendowifi.net', 'unresolved', {
      addedToListAtUtc: '2026-10-30T18:20:05Z'
    }),
    domain('ccs.cdn.wup.shop.nintendo.net', 'mismatched', { addedToListAtUtc: null })
  ]
};
const dated = nintendo.domains.filter((result) => result.addedToListAtUtc);

test('a failing name newer than the DNS server list shows its date, and its service one note', () => {
  translations.length = 0;
  const markup = renderToStaticMarkup(
    React.createElement(ServiceResultsList, {
      services: [nintendo],
      expandedServices: new Set(['nintendo']),
      onToggle: () => undefined,
      problemsOnly: false,
      contentReport: null,
      registerRef: () => undefined
    })
  );

  const notes = translations.filter((call) => call.key === NOTE_KEY);
  for (const result of dated) {
    assert.ok(
      notes.some((call) => call.vars.date === formatted(result.addedToListAtUtc)),
      `the ${result.status} row for ${result.originalEntry} carries addedToListAtUtc ${result.addedToListAtUtc} but shows no dated note: ${markup}`
    );
  }
  assert.equal(
    notes.length,
    dated.length,
    'only a row that carries addedToListAtUtc shows the note'
  );

  const summaries = translations.filter((call) => call.key === SERVICE_KEY);
  assert.equal(summaries.length, 1, `the service shows ${summaries.length} summary notes, not one`);
  assert.equal(summaries[0].vars.count, dated.length);
  assert.equal(
    summaries[0].vars.date,
    formatted('2026-10-30T18:20:05Z'),
    'the summary names the earliest date'
  );

  assert.equal(
    [...markup.matchAll(new RegExp(`class="[^"]*\\b${NOTE_CLASS}\\b[^"]*"`, 'g'))].length,
    dated.length + 1,
    'each dated row and the service summary carry the note class once'
  );
});
