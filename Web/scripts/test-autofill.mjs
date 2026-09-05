import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { collectNodes, compileToUrl, parseSource } from './transpile-module.mjs';

const credentialFields = new Map([
  ['components/initialization/AccessSetup.tsx', ['localUsername']],
  ['components/initialization/steps/AdminAccountStep.tsx', ['form.username']],
  ['components/initialization/steps/DatabaseSetupStep.tsx', ['form.username']],
  ['components/features/user/UserAccounts.tsx', ['editor.username', 'editor.password']],
  ['components/modals/auth/SteamAuthModal.tsx', ['username', 'password']],
  ['components/ui/CredentialFields.tsx', ['username', 'password']],
  ['components/ui/PostgresConnectionFields.tsx', ['values.username', 'values.password']],
  ['contexts/ConfigContext.tsx', ['username', 'password']]
]);

test('settings and non-credential fields exclude password-manager autofill', () => {
  const missing = [];
  for (const entry of readdirSync(new URL('../src/', import.meta.url), { recursive: true })) {
    if (!entry.endsWith('.tsx')) continue;
    const path = entry.replaceAll('\\', '/');
    if (path === 'components/ui/TextInput.tsx') continue;
    const source = parseSource(`src/${path}`, ts.ScriptKind.TSX);
    const controls = collectNodes(
      source,
      (node) =>
        (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) &&
        ['input', 'textarea', 'select'].includes(node.tagName.getText(source))
    );
    for (const control of controls) {
      const attributes = new Map(
        control.attributes.properties
          .filter(ts.isJsxAttribute)
          .map((attribute) => [attribute.name.getText(source), attribute.initializer])
      );
      const type = attributes.get('type');
      if (
        type &&
        ts.isStringLiteral(type) &&
        ['checkbox', 'radio', 'range', 'file', 'hidden', 'button', 'submit', 'reset'].includes(
          type.text
        )
      ) {
        continue;
      }
      const value = attributes.get('value');
      const expression =
        value && ts.isJsxExpression(value) ? value.expression?.getText(source) : '';
      const ignored = control.attributes.properties.some(
        (attribute) =>
          ts.isJsxSpreadAttribute(attribute) &&
          attribute.expression.getText(source) === 'noAutofill'
      );
      if (credentialFields.get(path)?.includes(expression)) {
        assert.equal(ignored, false, `${path}: account credentials must remain fillable`);
      } else if (path === 'components/ui/PasswordField.tsx') {
        assert.ok(control.getText(source).includes("autoComplete === 'off' ? noAutofill : {}"));
      } else if (!ignored) {
        const line = source.getLineAndCharacterOfPosition(control.getStart()).line + 1;
        missing.push(`${path}:${line}`);
      } else {
        assert.equal(
          attributes.has('autoComplete'),
          false,
          `${path}: use the shared autofill hints`
        );
      }
    }
  }
  assert.deepEqual(missing, [], `Fields missing autofill exclusions:\n${missing.join('\n')}`);
});

test('plain text input preserves caller-owned autofill exclusions', async () => {
  const { noAutofill } = await import(await compileToUrl('../src/utils/autofill.ts'));
  const textInput = parseSource('src/components/ui/TextInput.tsx', ts.ScriptKind.TSX);
  const [nativeInput] = collectNodes(
    textInput,
    (node) => ts.isJsxSelfClosingElement(node) && node.tagName.getText(textInput) === 'input'
  );
  assert.ok(nativeInput.getText(textInput).includes('{...inputProps}'));

  const platformSection = parseSource(
    'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillPlatformSection.tsx',
    ts.ScriptKind.TSX
  );
  const [scheduleName] = collectNodes(
    platformSection,
    (node) =>
      ts.isJsxSelfClosingElement(node) && node.tagName.getText(platformSection) === 'TextInput'
  );
  assert.ok(scheduleName.getText(platformSection).includes('{...noAutofill}'));
  assert.ok(scheduleName.getText(platformSection).includes('size="sm"'));
  assert.deepEqual(noAutofill, {
    autoComplete: 'off',
    'data-bwignore': 'true',
    'data-1p-ignore': 'true'
  });
});

test('numeric settings share autofill exclusions without losing native number bounds', async () => {
  const { noAutofill } = await import(await compileToUrl('../src/utils/autofill.ts'));
  assert.deepEqual(noAutofill, {
    autoComplete: 'off',
    'data-bwignore': 'true',
    'data-1p-ignore': 'true'
  });
  const source = parseSource('src/components/ui/NumberInput.tsx', ts.ScriptKind.TSX);
  const [input] = collectNodes(
    source,
    (node) => ts.isJsxSelfClosingElement(node) && node.tagName.getText(source) === 'input'
  );
  assert.ok(input.getText(source).includes('{...noAutofill}'));
  for (const attribute of ['type="number"', 'min={min}', 'max={max}', 'step={step}']) {
    assert.ok(input.getText(source).includes(attribute));
  }
});

test('clipboard fallback excludes autofill before mounting its temporary field', () => {
  const source = parseSource('src/utils/clipboard.ts').getText();
  const exclusions = source.indexOf('Object.entries(noAutofill)');
  assert.ok(exclusions > source.indexOf("document.createElement('textarea')"));
  assert.ok(exclusions < source.indexOf('document.body.appendChild(field)'));
  assert.ok(source.includes('field.setAttribute(name, value)'));
  assert.ok(source.includes('document.body.removeChild(field)'));
});
