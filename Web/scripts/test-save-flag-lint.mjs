import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { ESLint } from 'eslint';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const eslint = new ESLint({ cwd: webRoot });
const rejected = {
  'text input disabled by saving': '<TextInput disabled={saving} />',
  'dropdown disabled by loading': '<EnhancedDropdown disabled={loading} />',
  'native input disabled by a combined flag': '<input disabled={isScanBusy || saving} />',
  'text input disabled by a named saving flag': '<TextInput disabled={savingProfile} />',
  'setting row disabled by a named submitting flag': '<SettingRow disabled={isSubmittingForm} />'
};
const allowed = {
  'dropdown disabled by a scan lock': '<EnhancedDropdown disabled={isScanBusy} />',
  'button loading from saving': '<Button loading={saving} />',
  'button disabled by saving': '<Button disabled={saving} />'
};

async function saveFlagMessages(jsx) {
  const source = `export function Fixture({ saving, loading, isScanBusy, savingProfile, isSubmittingForm }) {\n  return <>${jsx}</>;\n}\n`;
  const [result] = await eslint.lintText(source, {
    filePath: resolve(webRoot, 'src/save-flag-fixture.tsx')
  });
  return result.messages.filter(
    (message) =>
      message.ruleId === 'no-restricted-syntax' &&
      message.message.startsWith('Buttons never flicker')
  );
}

for (const [name, jsx] of Object.entries(rejected)) {
  test(`rejects ${name}`, async () => {
    assert.equal((await saveFlagMessages(jsx)).length, 1);
  });
}

for (const [name, jsx] of Object.entries(allowed)) {
  test(`allows ${name}`, async () => {
    assert.equal((await saveFlagMessages(jsx)).length, 0);
  });
}
