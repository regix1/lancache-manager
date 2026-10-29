import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

const webRoot = resolve(import.meta.dirname, '..');
const networkSection = readFileSync(
  resolve(webRoot, 'src/components/features/prefill/NetworkStatusSection.tsx'),
  'utf8'
);
const english = JSON.parse(readFileSync(resolve(webRoot, 'src/i18n/locales/en.json'), 'utf8'));

test('prefill diagnostics do not expose container command failures', () => {
  assert.doesNotMatch(networkSection, /diagnostics\.internetConnectivityError/);
  assert.doesNotMatch(networkSection, /result\.error/);
  // A failed check says Failed / Not resolved on its own row; no box repeats it underneath.
  assert.doesNotMatch(networkSection, /prefill\.network\.(internetCheckFailed|dnsCheckFailed)/);
});

test('an unanswered docker exec leaves the internet check not tested', () => {
  assert.match(networkSection, /const internetNotRun = diagnostics\.internetConnectivity == null/);
  assert.match(networkSection, /\{internetFailed && <NetworkTroubleshooting \/>\}/);
  assert.doesNotMatch(networkSection, /!diagnostics\.internetConnectivity/);
});

// The card is collapsed by default, so its header line and box color are what a person reads.
// The real component is compiled and rendered; only its UI children and icons are stubbed.
// The automatic JSX runtime emits a double-quoted import that the alias rewrite does not match,
// so the classic transform is used and its React.createElement resolves through this global.
globalThis.React = React;
const stub = (name) =>
  moduleUrl(
    `export const ${name} = (props) => React.createElement('div', { className: props.className }, props.children);`
  );
const { NetworkStatusSection } = await import(
  await compileToUrl(
    '../src/components/features/prefill/NetworkStatusSection.tsx',
    {
      react: import.meta.resolve('react'),
      'react-i18next': moduleUrl('export const useTranslation = () => ({ t: (key) => key });'),
      'lucide-react': moduleUrl(
        'export const CheckCircle2 = () => null, XCircle = CheckCircle2, AlertTriangle = CheckCircle2, Wifi = CheckCircle2, Info = CheckCircle2, ChevronDown = CheckCircle2;'
      ),
      '../../ui/Alert': stub('Alert'),
      '../../ui/Button': stub('Button'),
      '../../ui/Card': stub('Card'),
      '../../ui/CollapsibleRegion': stub('CollapsibleRegion')
    },
    { jsx: typescript.JsxEmit.React }
  )
);

const dnsRow = (domain, success, isPrivateIp) => ({
  domain,
  success,
  isPrivateIp,
  resolvedIps: success ? ['10.0.0.5'] : []
});

const header = (diagnostics) => {
  const html = renderToStaticMarkup(
    React.createElement(NetworkStatusSection, {
      diagnostics: {
        testedAt: '2026-10-26T00:00:00Z',
        lancacheIpInjected: '10.0.0.5',
        ...diagnostics
      }
    })
  );
  return {
    subtitle: html.match(/<p class="text-sm text-themed-muted">([^<]+)<\/p>/)[1],
    box: html.match(/prefill-network-status-box--(\w+)/)[1]
  };
};

const primaryOkSecondFailed = [
  dnsRow('lancache.steamcontent.com', true, true),
  dnsRow('steam.cache.lancache.net', false, false)
];

test('a finished sweep with a domain that did not resolve reports an issue', () => {
  assert.deepEqual(
    header({ internetConnectivity: true, dnsFinished: true, dnsResults: primaryOkSecondFailed }),
    { subtitle: 'prefill.network.issuesDetected', box: 'warning' }
  );
});

test('host networking with its only domain unresolved reports an issue', () => {
  assert.deepEqual(
    header({
      internetConnectivity: true,
      useHostNetworking: true,
      dnsFinished: true,
      dnsResults: [dnsRow('lancache.steamcontent.com', false, false)]
    }),
    { subtitle: 'prefill.network.issuesDetected', box: 'warning' }
  );
});

test('a stopped sweep is unfinished, never a full pass, whatever its stored rows say', () => {
  const stopped = { subtitle: 'prefill.network.checksDidNotFinish', box: 'info' };
  assert.deepEqual(
    header({ internetConnectivity: true, dnsFinished: false, dnsResults: primaryOkSecondFailed }),
    stopped
  );
  assert.deepEqual(
    header({
      internetConnectivity: true,
      dnsFinished: false,
      dnsResults: [dnsRow('lancache.steamcontent.com', true, true)]
    }),
    stopped
  );
  assert.deepEqual(
    header({ internetConnectivity: true, dnsFinished: false, dnsResults: [] }),
    stopped
  );
});

test('a failed internet check is an issue and an unanswered one is not tested', () => {
  assert.deepEqual(header({ internetConnectivity: false, dnsFinished: false, dnsResults: [] }), {
    subtitle: 'prefill.network.issuesDetected',
    box: 'warning'
  });
  assert.deepEqual(header({ internetConnectivity: null, dnsFinished: false, dnsResults: [] }), {
    subtitle: 'prefill.network.notTested',
    box: 'info'
  });
});

test('a finished sweep where every domain resolves privately passes', () => {
  assert.deepEqual(
    header({
      internetConnectivity: true,
      dnsFinished: true,
      dnsResults: [
        dnsRow('lancache.steamcontent.com', true, true),
        dnsRow('steam.cache.lancache.net', true, true)
      ]
    }),
    { subtitle: 'prefill.network.allChecksPassed', box: 'success' }
  );
});

test('prefill troubleshooting explains the DNS override', () => {
  assert.match(networkSection, /Prefill__NetworkMode=bridge/);
  assert.match(networkSection, /Prefill__LancacheDnsIp=&lt;DNS-server-IP&gt;/);
  assert.match(english.prefill.network.lancacheDnsIpInstruction, /DNS server/);
  assert.match(english.prefill.network.lancacheDnsIpExplanation, /lancache-dns/);
  assert.match(english.prefill.network.lancacheDnsIpExplanation, /new prefill session/);
});
