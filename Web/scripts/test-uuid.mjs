import assert from 'node:assert/strict';
import test from 'node:test';

import { compileToUrl } from './transpile-module.mjs';

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

const originalCrypto = globalThis.crypto;

const setCrypto = (crypto) => {
  Object.defineProperty(globalThis, 'crypto', {
    configurable: true,
    value: crypto
  });
};

const restoreCrypto = () => setCrypto(originalCrypto);

const loadUuid = async () => import(await compileToUrl('../src/utils/uuid.ts'));

test('createUuid uses native randomUUID when it is available', async (t) => {
  const expected = '123e4567-e89b-42d3-a456-426614174000';
  let calls = 0;
  setCrypto({
    randomUUID() {
      calls += 1;
      return expected;
    },
    getRandomValues() {
      throw new Error('native UUID generation should not fall back to random bytes');
    }
  });
  t.after(restoreCrypto);

  const { createUuid } = await loadUuid();
  assert.equal(createUuid(), expected);
  assert.equal(calls, 1);
});

test('createUuid produces RFC 4122 version 4 identifiers without randomUUID', async (t) => {
  let value = 0;
  setCrypto({
    getRandomValues(bytes) {
      for (let index = 0; index < bytes.length; index += 1) {
        bytes[index] = value + index;
      }
      value += 17;
      return bytes;
    }
  });
  t.after(restoreCrypto);

  const { createUuid } = await loadUuid();
  const first = createUuid();
  const second = createUuid();

  assert.match(first, uuidPattern);
  assert.match(second, uuidPattern);
  assert.notEqual(first, second);
});

test('the scheduled-prefill edit-session identifier uses the shared UUID generator', async (t) => {
  setCrypto({
    getRandomValues(bytes) {
      bytes.fill(0);
      return bytes;
    }
  });
  t.after(restoreCrypto);

  const uuidUrl = await compileToUrl('../src/utils/uuid.ts');
  const ledgerUrl = await compileToUrl(
    '../src/components/features/management/schedules/scheduled-prefill/scheduledPrefillEditSessionLedger.ts',
    { '@utils/uuid': uuidUrl }
  );
  const { createScheduledPrefillEditSessionId } = await import(ledgerUrl);

  assert.match(createScheduledPrefillEditSessionId(), uuidPattern);
});
