import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  findSoleNode,
  liftConstArrow,
  parseSource,
  translator
} from './transpile-module.mjs';

/**
 * The database import form shows each check and import result from its i18n key. The failed
 * connection check used to carry no key, so a Chinese reader got the driver's English sentence;
 * these cases run the shipped handlers and render each stored result in Chinese.
 */

const componentPath = 'src/components/features/management/data/DatabaseImportForm.tsx';
const driverText = 'password authentication failed for user "lancache"';

const chinese = /[一-鿿]/;
const t = translator('zh');

/** The text the form's alert shows for a stored check result: the shipped expression, run. */
const renderCheck = (() => {
  const form = parseSource(componentPath);
  const shown = findSoleNode(
    form,
    'validation result text',
    (node) =>
      typescript.isJsxExpression(node) &&
      node.expression !== undefined &&
      node.expression.getText(form).startsWith('t(validationResult.stageKey')
  );
  return bindLifted(`(validationResult) => ${shown.expression.getText(form)}`, { t });
})();

const assertChineseWithDetail = (text, detail) => {
  assert.match(text, chinese, `expected a Chinese sentence, got: ${text}`);
  assert.ok(text.includes(detail), `expected the driver text inside: ${text}`);
  assert.doesNotMatch(text, /Connection failed|Import failed|undefined/);
};

const buildForm = ({ connectionString, api }) => {
  const state = { validationResult: null, importResult: null };
  const bindings = {
    t,
    getEffectiveConnectionString: () => connectionString,
    setValidationResult: (value) => {
      state.validationResult = value;
    },
    setValidating: () => undefined,
    setImporting: () => undefined,
    setImportResult: (value) => {
      state.importResult = value;
    },
    setProgress: () => undefined,
    onImportComplete: () => undefined,
    batchSize: 1000,
    overwriteExisting: false,
    validationResult: {
      valid: true,
      message: '',
      stageKey: 'management.database.import.connected'
    },
    ApiService: api,
    getErrorMessage: (error) => error.message
  };
  return {
    state,
    validate: bindLifted(liftConstArrow(componentPath, 'handleValidate'), bindings),
    runImport: bindLifted(liftConstArrow(componentPath, 'handleImport'), bindings)
  };
};

test('a failed check from the server shows the driver text inside the Chinese sentence', async () => {
  const form = buildForm({
    connectionString: 'Host=x',
    api: {
      validateMigrationConnection: async () => ({
        valid: false,
        message: driverText,
        stageKey: 'management.database.import.connectionFailed'
      })
    }
  });
  await form.validate();

  assertChineseWithDetail(renderCheck(form.state.validationResult), driverText);
});

test('a check request that throws stores a keyed result', async () => {
  const form = buildForm({
    connectionString: 'Host=x',
    api: {
      validateMigrationConnection: async () => {
        throw new Error(driverText);
      }
    }
  });
  await form.validate();

  assert.equal(form.state.validationResult.stageKey, 'management.database.import.connectionFailed');
  assertChineseWithDetail(renderCheck(form.state.validationResult), driverText);
});

test('an empty connection string stores a keyed result', async () => {
  const form = buildForm({ connectionString: '   ', api: {} });
  await form.validate();

  assert.equal(form.state.validationResult.stageKey, 'initialization.importHistorical.pleaseEnter');
  assert.match(renderCheck(form.state.validationResult), chinese);
});

test('an import request that throws shows the detail inside the Chinese sentence', async () => {
  const form = buildForm({
    connectionString: 'Host=x',
    api: {
      importFromLancacheManager: async () => {
        throw new Error(driverText);
      }
    }
  });
  await form.runImport();

  assert.equal(
    form.state.validationResult.stageKey,
    'initialization.importHistorical.importFailed'
  );
  assertChineseWithDetail(renderCheck(form.state.validationResult), driverText);
});

test('the form renders results from their key alone', () => {
  const source = readFileSync(new URL(`../${componentPath}`, import.meta.url), 'utf8');
  // The leading space finds a ternary arm and not the `detail:` property the render passes.
  for (const text of ['defaultValue', ' : validationResult.message', ' : importResult.message']) {
    assert.ok(!source.includes(text), `${componentPath} still has ${text}`);
  }
});
