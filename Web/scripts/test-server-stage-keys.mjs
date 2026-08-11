import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import ts from 'typescript';

/**
 * The API names the reason for a failure as an i18n key beside the English sentence, and the browser
 * renders the key. Nothing else checks that those keys exist: validate-i18n-keys.mjs counts a value
 * it cannot resolve at build time and never fails on it, and validate-stage-keys.mjs cross-checks
 * only the `signalr.` notification keys. So a key the server emits and no locale carries reads as
 * English forever, in every language, and no gate says a word.
 *
 * This reads the real sources on both sides: the keys out of the C# that emits them, and the
 * expression out of the screen that renders them.
 */

const repoFile = (relativePath) => new URL(`../../${relativePath}`, import.meta.url);
const webFile = (relativePath) => new URL(`../${relativePath}`, import.meta.url);

const en = JSON.parse(readFileSync(webFile('src/i18n/locales/en.json'), 'utf8'));
const zh = JSON.parse(readFileSync(webFile('src/i18n/locales/zh.json'), 'utf8'));

/** What i18next resolves a dotted key to, or undefined when the bundle has no string there. */
const lookup = (bundle, key) => {
  const value = key
    .split('.')
    .reduce(
      (node, segment) => (node && typeof node === 'object' ? node[segment] : undefined),
      bundle
    );
  return typeof value === 'string' ? value : undefined;
};

/**
 * i18next's own behaviour for the two calls the product makes: a key the bundle carries wins, and a
 * key it does not falls back to `defaultValue`, or to the key path when there is no default.
 */
const translator = (bundle) => (key, options) =>
  lookup(bundle, key) ?? options?.defaultValue ?? key;

// ---------------------------------------------------------------------------
// The keys the C# side emits
// ---------------------------------------------------------------------------

const apiRoot = path.resolve(repoFile('Api/LancacheManager').pathname.slice(1));

const csharpSources = readdirSync(apiRoot, { recursive: true, withFileTypes: true })
  .filter((entry) => entry.isFile() && entry.name.endsWith('.cs'))
  .map((entry) => path.join(entry.parentPath ?? entry.path, entry.name))
  .filter(
    (file) =>
      !file.includes(`${path.sep}bin${path.sep}`) && !file.includes(`${path.sep}obj${path.sep}`)
  );

/** Comment lines, so a key quoted in prose as an example is not read as one the server emits. */
const withoutComments = (source) =>
  source
    .split('\n')
    .filter((line) => {
      const start = line.trimStart();
      return !start.startsWith('//') && !start.startsWith('*') && !start.startsWith('/*');
    })
    .join('\n');

const KEY_PATH = /^[a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+$/;

const serverKeys = (() => {
  const constants = new Map();
  const keys = new Map();

  const record = (key, file) => {
    if (!KEY_PATH.test(key)) return;
    // The notification keys have their own cross-check in validate-stage-keys.mjs.
    if (key.startsWith('signalr.')) return;
    if (!keys.has(key)) keys.set(key, new Set());
    keys.get(key).add(path.basename(file));
  };

  const sources = csharpSources.map((file) => [file, withoutComments(readFileSync(file, 'utf8'))]);

  for (const [, source] of sources) {
    for (const [, name, value] of source.matchAll(/const\s+string\s+(\w+)\s*=\s*"([^"]*)"/g)) {
      constants.set(name, value);
    }
  }

  for (const [file, source] of sources) {
    // Assigned to StageKey directly, or through a constant named beside the response it belongs to.
    for (const [, assigned] of source.matchAll(/StageKey\s*=\s*([^,;\r\n]+)/g)) {
      const expression = assigned.trim();
      if (expression.startsWith('"')) {
        record(expression.slice(1, expression.indexOf('"', 1)), file);
        continue;
      }
      if (/^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(expression)) {
        const constant = constants.get(expression.split('.').pop());
        if (constant !== undefined) record(constant, file);
      }
    }

    // `errors.` is the namespace this API refuses under, so a key in it is one the browser is meant
    // to render however it reached the response.
    for (const [, literal] of source.matchAll(/"(errors\.[^"]*)"/g)) {
      record(literal, file);
    }
  }

  return keys;
})();

test('every error key the API emits is translated in both locales', () => {
  assert.ok(serverKeys.size > 0, 'found no server-emitted keys, so this check proves nothing');

  const untranslated = [];
  for (const [key, files] of serverKeys) {
    const where = [...files].join(', ');
    if (lookup(en, key) === undefined) untranslated.push(`en.json is missing ${key} (${where})`);
    if (lookup(zh, key) === undefined) untranslated.push(`zh.json is missing ${key} (${where})`);
  }

  assert.deepEqual(untranslated, []);
});

test('the account setup refusals are among the keys that were checked', () => {
  // Without this the check above passes by finding nothing, which is what it would do the day the
  // response stops carrying a key at all.
  for (const key of [
    'errors.accountSetup.apiKeyRequired',
    'errors.accountSetup.claimWindowClosed',
    'errors.accountSetup.accountExists'
  ]) {
    assert.ok(serverKeys.has(key), `${key} is not emitted by any C# source`);
  }
});

// ---------------------------------------------------------------------------
// What the account step does with the key it is given
// ---------------------------------------------------------------------------

const stepSource = readFileSync(
  webFile('src/components/initialization/steps/AdminAccountStep.tsx'),
  'utf8'
);
const stepFile = ts.createSourceFile(
  'AdminAccountStep.tsx',
  stepSource,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TSX
);

const collect = (sourceFile, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) found.push(node);
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);
  return found;
};

/** The expression the screen actually shows, read out of the product source rather than copied. */
const submitErrorExpression = (() => {
  const calls = collect(
    stepFile,
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(stepFile) === 'setSubmitError' &&
      node.arguments.length === 1 &&
      node.arguments[0].getText(stepFile).includes('stageKey')
  );
  assert.equal(
    calls.length,
    1,
    'expected exactly one setSubmitError call that reads the stage key'
  );
  return calls[0].arguments[0].getText(stepFile);
})();

const sentenceExpression = (() => {
  const declarations = collect(
    stepFile,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(stepFile) === 'sentence'
  );
  assert.equal(declarations.length, 1, 'expected exactly one sentence declaration');
  return declarations[0].initializer.getText(stepFile);
})();

const evaluate = (expression, bindings) => {
  const names = Object.keys(bindings);
  return new Function(...names, `return (${expression});`)(...names.map((name) => bindings[name]));
};

/** What the operator reads for one response body, in one language. */
const shownFor = (data, bundle) => {
  const t = translator(bundle);
  const sentence = evaluate(sentenceExpression, { data, t });
  return evaluate(submitErrorExpression, { data, t, sentence });
};

test('a key both locales carry is read in the language the operator picked', () => {
  const refused = {
    stageKey: 'errors.accountSetup.accountExists',
    error: 'An account already exists on this installation'
  };

  assert.equal(shownFor(refused, en), en.errors.accountSetup.accountExists);
  assert.equal(shownFor(refused, zh), zh.errors.accountSetup.accountExists);
  assert.notEqual(shownFor(refused, zh), refused.error);
});

test('a key this build has no translation for shows the English sentence, not the key path', () => {
  const refused = {
    stageKey: 'errors.accountSetup.somethingAddedLater',
    error: 'A newer server refused this for a reason this build has no words for'
  };

  assert.equal(shownFor(refused, zh), refused.error);
});

test('a response with no key at all still reads as a sentence', () => {
  assert.equal(shownFor({ error: 'Validation failed' }, zh), 'Validation failed');
  assert.equal(
    shownFor({ errors: [{ field: 'password', message: 'Password is too short' }] }, zh),
    'Password is too short'
  );
  assert.equal(shownFor({}, zh), zh.initialization.adminAccount.errors.createFailed);
});
