import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';

const webRoot = resolve(import.meta.dirname, '..');
const networkSection = readFileSync(
  resolve(webRoot, 'src/components/features/prefill/NetworkStatusSection.tsx'),
  'utf8'
);
const english = JSON.parse(readFileSync(resolve(webRoot, 'src/i18n/locales/en.json'), 'utf8'));

test('prefill diagnostics do not expose container command failures', () => {
  assert.doesNotMatch(networkSection, /diagnostics\.internetConnectivityError/);
  assert.doesNotMatch(networkSection, /result\.error/);
  assert.match(networkSection, /prefill\.network\.internetCheckFailed/);
  assert.match(networkSection, /prefill\.network\.dnsCheckFailed/);
});

test('prefill troubleshooting explains the DNS override', () => {
  assert.match(networkSection, /Prefill__NetworkMode=bridge/);
  assert.match(networkSection, /Prefill__LancacheDnsIp=&lt;DNS-server-IP&gt;/);
  assert.match(english.prefill.network.lancacheDnsIpInstruction, /DNS server/);
  assert.match(english.prefill.network.lancacheDnsIpExplanation, /lancache-dns/);
  assert.match(english.prefill.network.lancacheDnsIpExplanation, /new prefill session/);
});
