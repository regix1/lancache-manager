import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import {
  assertClean,
  collectNodes,
  location,
  normalizePath,
  parseSource,
  repository,
  sourceFiles,
  sourceProgram,
  tokens
} from './source-contracts.mjs';

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

if (process.argv.includes('--reject-control'))
  assertClean(checkLocales(['errors.missing'], [en, zh]));

// ---------------------------------------------------------------------------
// The keys the C# side emits
// ---------------------------------------------------------------------------

const csharpSources = sourceFiles();
const KEY_PATH = /^[a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+$/;

export function readEmissions(entries) {
  const keys = new Map();
  const violations = [];
  const record = (key, file) => {
    if (!KEY_PATH.test(key)) return;
    if (!keys.has(key)) keys.set(key, new Set());
    keys.get(key).add(path.basename(file));
  };
  const constants = new Map();
  const prefixes = new Map();
  const parsed = entries
    .filter(({ file }) => !/\.tsx?$/.test(file))
    .map(({ file, text }) => ({ file, words: tokens(text, file) }));
  for (const { file, words } of parsed) {
    const scopes = [];
    for (let index = 0; index < words.length; index++) {
      while (scopes.length && index > scopes.at(-1).end) scopes.pop();
      if (['class', 'record', 'struct'].includes(words[index].text)) {
        let start = index + 2;
        while (start < words.length && !['{', ';'].includes(words[start].text)) start++;
        if (words[start]?.text === '{')
          scopes.push({ name: words[index + 1].text, end: words[start].pair });
      }
      words[index].scope = scopes.map((scope) => scope.name).join('.');
      if (
        words[index].text === 'const' &&
        words[index + 3]?.text === '=' &&
        words[index + 4]?.value !== undefined
      ) {
        const name = `${words[index].scope}.${words[index + 2].text}`;
        constants.set(`${file}:${name}`, words[index + 4].value);
        constants.set(name, words[index + 4].value);
        words[index + 4].constant = name;
      }
    }
  }
  for (const { words } of parsed) {
    for (let index = 0; index < words.length; index++) {
      if (words[index].text !== 'record' || words[index + 2]?.text !== '(') continue;
      const type = words[index + 1].text;
      const start = index + 2;
      const parameters = [];
      for (let field = start + 1; field < words[start].pair; field++)
        if ([',', ')'].includes(words[field + 1]?.text)) parameters.push(words[field].text);
      parameters.forEach((name, argument) => {
        if (!name.endsWith('Prefix')) return;
        const values = new Set();
        for (const entry of parsed)
          for (let offset = 0; offset < entry.words.length; offset++) {
            const sequence = entry.words;
            if (
              sequence[offset].text !== type ||
              sequence[offset + 2]?.text !== '=' ||
              sequence[offset + 3]?.text !== 'new' ||
              sequence[offset + 4]?.text !== '('
            )
              continue;
            const opening = offset + 4;
            const argumentsList = [[]];
            for (let item = opening + 1; item < sequence[opening].pair; item++) {
              if (sequence[item].text === ',') argumentsList.push([]);
              else {
                argumentsList.at(-1).push(sequence[item]);
                if (['(', '{', '['].includes(sequence[item].text)) item = sequence[item].pair;
              }
            }
            const value = argumentsList[argument];
            if (value?.length === 1 && value[0].value !== undefined) {
              values.add(value[0].value);
              value[0].prefix = true;
            } else violations.push(`Unresolved ${type}.${name} constructor argument`);
          }
        if (values.size) prefixes.set(`${type}.${name}`, values);
      });
    }
  }
  for (const { file, words } of parsed) {
    const receivers = new Map();
    for (let index = 0; index < words.length - 1; index++)
      if ([...prefixes.keys()].some((key) => key.startsWith(words[index].text + '.')))
        receivers.set(words[index + 1].text, words[index].text);
    const usedPrefixes = new Set();
    for (const token of words)
      for (const match of token.value?.matchAll(/\{([\w.]+)\}\./g) ?? [])
        usedPrefixes.add(`${token.scope}.${match[1]}`);
    function expand(expression, token) {
      const literal =
        constants.get(`${file}:${token.scope}.${expression}`) ?? constants.get(expression);
      if (literal !== undefined) return [literal];
      const member = expression.match(/^(\w+)\.(\w+)$/);
      if (member) {
        const values = prefixes.get(`${receivers.get(member[1])}.${member[2]}`);
        if (values) return [...values];
      }
      const call = expression.match(/^(\w+)\(/);
      if (call)
        for (let index = 0; index < words.length; index++) {
          if (
            words[index].text !== call[1] ||
            words[index - 1]?.text !== 'string' ||
            words[index + 1]?.text !== '('
          )
            continue;
          const start = words[index + 1].pair + 1;
          if (words[start]?.text !== '=>') continue;
          const values = [];
          for (let arm = start + 1; arm < words.length && words[arm].text !== ';'; arm++)
            if (words[arm].value !== undefined) values.push(words[arm].value);
          if (values.length) return values;
        }
      return undefined;
    }
    for (let index = 0; index < words.length; index++) {
      const token = words[index];
      if (
        token.value &&
        !token.prefix &&
        !usedPrefixes.has(token.constant) &&
        /^(?:errors|signalr)\./.test(token.value) &&
        !['+', '.'].includes(words[index + 1]?.text) &&
        !['StartsWith', 'Contains'].includes(words[index - 2]?.text)
      )
        record(token.value, file);
      if (token.value && words[index - 1]?.text === '$') {
        let values = [''];
        let offset = 0;
        let relevant = false;
        for (const match of token.value.matchAll(/\{([^{}]+)\}/g)) {
          const choices = expand(match[1], token);
          if (!choices) {
            values = [];
            break;
          }
          relevant ||= choices.some((choice) => /^(?:errors|signalr)\./.test(choice));
          values = values.flatMap((prefix) =>
            choices.map((choice) => prefix + token.value.slice(offset, match.index) + choice)
          );
          offset = match.index + match[0].length;
        }
        if (relevant || /^(?:[Tt]itle)?[Ss]tageKey$/.test(words[index - 3]?.text ?? '')) {
          if (!values.length)
            violations.push(`${normalizePath(file)}:${token.line}: unresolved composed stage key`);
          for (const value of values) record(value + token.value.slice(offset), file);
        }
      }
      if (
        !/^(?:[Tt]itle)?[Ss]tageKey$/.test(token.text) ||
        !['=', ':'].includes(words[index + 1]?.text)
      )
        continue;
      const expression = [];
      let end = index + 2;
      for (; end < words.length && ![',', ';', '}'].includes(words[end].text); end++)
        expression.push(words[end]);
      if (expression.some((word) => word.value !== undefined)) {
        for (const word of expression) if (word.value !== undefined) record(word.value, file);
        continue;
      }
      const name = expression.map((word) => word.text).join('');
      const value = constants.get(`${file}:${token.scope}.${name}`) ?? constants.get(name);
      if (value !== undefined) record(value, file);
      else if (/^[A-Z]\w*(?:\.[A-Z]\w*)*$/.test(name) && !/StageKey$/.test(name))
        violations.push(
          `${normalizePath(file)}:${token.line}: unresolved stage-key constant ${name}`
        );
    }
  }
  const scripts = entries.filter(({ file }) => /\.tsx?$/.test(file));
  const program = sourceProgram(scripts);
  const checker = program.getTypeChecker();
  const propertyValues = new Map();
  const prefixSymbols = new Set();
  function symbolOf(node) {
    let symbol = checker.getSymbolAtLocation(node);
    if (symbol?.flags & ts.SymbolFlags.Alias) symbol = checker.getAliasedSymbol(symbol);
    const declaration = symbol?.declarations?.[0];
    if (declaration && ts.isBindingElement(declaration)) {
      const owner = declaration.parent.parent;
      const receiver = ts.isVariableDeclaration(owner) ? owner.initializer : owner;
      if (receiver)
        symbol = checker
          .getTypeAtLocation(receiver)
          .getProperty(declaration.propertyName?.getText() ?? declaration.name.getText());
    }
    return symbol;
  }
  for (const { file } of scripts) {
    const source = program.getSourceFile(file);
    for (const node of collectNodes(source, (node) => ts.isTemplateExpression(node)))
      for (const span of node.templateSpans) {
        const symbol = symbolOf(span.expression);
        if (symbol) prefixSymbols.add(symbol);
      }
    for (const node of collectNodes(
      source,
      (node) => ts.isPropertyAssignment(node) && ts.isStringLiteral(node.initializer)
    )) {
      const symbol = checker.getContextualType(node.parent)?.getProperty(node.name.getText(source));
      if (!symbol) continue;
      if (!propertyValues.has(symbol)) propertyValues.set(symbol, new Set());
      propertyValues.get(symbol).add(node.initializer.text);
    }
  }
  function resolve(node, seen = new Set()) {
    if (!node || seen.has(node)) return undefined;
    seen.add(node);
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) return [node.text];
    if (
      ts.isParenthesizedExpression(node) ||
      ts.isAsExpression(node) ||
      ts.isSatisfiesExpression(node)
    )
      return resolve(node.expression, seen);
    if (ts.isConditionalExpression(node)) {
      const yes = resolve(node.whenTrue, new Set(seen));
      const no = resolve(node.whenFalse, new Set(seen));
      return yes && no ? [...yes, ...no] : undefined;
    }
    if (ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.PlusToken) {
      const left = resolve(node.left, new Set(seen));
      const right = resolve(node.right, new Set(seen));
      return left && right
        ? left.flatMap((prefix) => right.map((suffix) => prefix + suffix))
        : undefined;
    }
    if (ts.isTemplateExpression(node)) {
      let values = [node.head.text];
      for (const span of node.templateSpans) {
        const choices = resolve(span.expression, new Set(seen));
        if (!choices) return undefined;
        values = values.flatMap((prefix) =>
          choices.map((choice) => prefix + choice + span.literal.text)
        );
      }
      return values;
    }
    const symbol = symbolOf(node);
    if (propertyValues.has(symbol)) return [...propertyValues.get(symbol)];
    for (const declaration of symbol?.declarations ?? [])
      if (ts.isVariableDeclaration(declaration) && declaration.initializer)
        return resolve(declaration.initializer, seen);
    const type = checker.getTypeAtLocation(node);
    const choices = type.isUnion() ? type.types : [type];
    if (choices.length && choices.every((choice) => choice.isStringLiteral()))
      return choices.map((choice) => choice.value);
    return undefined;
  }
  for (const { file } of scripts) {
    const source = program.getSourceFile(file);
    for (const node of collectNodes(source, (node) => ts.isStringLiteral(node))) {
      if (!/^(?:errors|signalr)\./.test(node.text)) continue;
      if (
        ts.isBinaryExpression(node.parent) &&
        node.parent.operatorToken.kind === ts.SyntaxKind.PlusToken
      )
        continue;
      if (
        ts.isCallExpression(node.parent) &&
        ts.isPropertyAccessExpression(node.parent.expression) &&
        ['startsWith', 'includes', 'replace'].includes(node.parent.expression.name.text)
      )
        continue;
      if (ts.isVariableDeclaration(node.parent) && prefixSymbols.has(symbolOf(node.parent.name)))
        continue;
      if (
        ts.isPropertyAssignment(node.parent) &&
        prefixSymbols.has(
          checker
            .getContextualType(node.parent.parent)
            ?.getProperty(node.parent.name.getText(source))
        )
      )
        continue;
      record(node.text, file);
    }
    for (const node of collectNodes(
      source,
      (node) =>
        ts.isTemplateExpression(node) ||
        (ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.PlusToken)
    )) {
      const values = resolve(node);
      if (values) {
        for (const value of values) if (/^(?:errors|signalr)\./.test(value)) record(value, file);
      } else if (
        /^[`"'](?:errors|signalr)\./.test(node.getText(source)) ||
        (ts.isTemplateExpression(node) &&
          node.templateSpans.some((span) =>
            resolve(span.expression)?.some((value) => /^(?:errors|signalr)\./.test(value))
          ))
      ) {
        violations.push(`${location(source, node)}: unresolved composed stage key`);
      }
    }
    for (const node of collectNodes(
      source,
      (node) =>
        ts.isPropertyAssignment(node) &&
        /^(?:titleStageKey|stageKey)$/.test(node.name.getText(source))
    )) {
      const values = resolve(node.initializer);
      if (values) values.forEach((value) => record(value, file));
      else if (
        ts.isIdentifier(node.initializer) &&
        /^[A-Z_][A-Z_0-9]*$/.test(node.initializer.text)
      )
        violations.push(
          `${location(source, node)}: unresolved stage-key constant ${node.initializer.text}`
        );
    }
  }
  return { keys, violations };
}

const emitted = readEmissions(csharpSources);
const serverKeys = emitted.keys;

export function checkLocales(keys, bundles) {
  const violations = [];
  const placeholders = (text) =>
    [...text.matchAll(/{{\s*-?\s*([\w.]+)[^}]*}}/g)].map((match) => match[1]).sort();
  for (const key of keys) {
    const sentences = bundles.map((bundle) => lookup(bundle, key));
    sentences.forEach((sentence, index) => {
      if (sentence === undefined) violations.push(`locale ${index} is missing string ${key}`);
    });
    if (
      sentences.every((sentence) => sentence !== undefined) &&
      JSON.stringify(placeholders(sentences[0])) !== JSON.stringify(placeholders(sentences[1]))
    )
      violations.push(`placeholder mismatch for ${key}`);
  }
  return violations;
}

test('every error key the API emits is translated in both locales', () => {
  assert.ok(serverKeys.size > 0, 'found no server-emitted keys, so this check proves nothing');
  console.log(
    `Message source counts: ${csharpSources.length} files, ${serverKeys.size} emitted keys`
  );
  assertClean([
    ...emitted.violations,
    ...checkLocales(serverKeys.keys(), [en, zh]).map((issue) => {
      const key = issue.split(' ').at(-1);
      return `${issue} (${[...(serverKeys.get(key) ?? [])].join(', ')})`;
    })
  ]);
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

export function checkMessages(entries) {
  const program = sourceProgram(entries.filter(({ file }) => /\.tsx?$/.test(file)));
  const checker = program.getTypeChecker();
  const violations = [];
  let roots = 0;
  for (const { file } of entries.filter(({ file }) => /\.tsx?$/.test(file))) {
    const source = program.getSourceFile(file);
    if (!source) throw new Error(`Source was not parsed: ${file}`);
    function normalized(node, seen = new Set()) {
      if (!node || seen.has(node)) return false;
      seen.add(node);
      if (
        ts.isParenthesizedExpression(node) ||
        ts.isAsExpression(node) ||
        ts.isNonNullExpression(node)
      )
        return normalized(node.expression, seen);
      if (ts.isPrefixUnaryExpression(node)) return normalized(node.operand, seen);
      if (ts.isCallExpression(node)) {
        let symbol = checker.getSymbolAtLocation(node.expression);
        if (symbol?.flags & ts.SymbolFlags.Alias) symbol = checker.getAliasedSymbol(symbol);
        if (
          symbol?.name === 'getErrorMessage' &&
          symbol.declarations?.some((declaration) =>
            normalizePath(declaration.getSourceFile().fileName).endsWith('/utils/error.ts')
          )
        )
          return true;
        if (
          ts.isPropertyAccessExpression(node.expression) &&
          [
            'getCancelledMessage',
            'getFailureMessage',
            'getSuccessMessage',
            'getCompletedMessage'
          ].includes(node.expression.name.text)
        ) {
          const signature = checker
            .getNonNullableType(checker.getTypeAtLocation(node.expression))
            .getCallSignatures()[0];
          return Boolean(
            signature &&
            checker.getReturnTypeOfSignature(signature).flags & ts.TypeFlags.String &&
            signature.declaration &&
            /\/contexts\/notifications\/(?:types|handlers)\.ts$/.test(
              normalizePath(signature.declaration.getSourceFile().fileName)
            )
          );
        }
        if (ts.isPropertyAccessExpression(node.expression) && node.expression.name.text === 'trim')
          return normalized(node.expression.expression, seen);
      }
      if (ts.isPropertyAccessExpression(node) && node.name.text === 'message') {
        const receiver = checker.getTypeAtLocation(node.expression);
        const names = (receiver.isUnion() ? receiver.types : [receiver]).map(
          (entry) => entry.aliasSymbol?.name ?? entry.symbol?.name
        );
        return (
          names.length > 0 &&
          names.every((name) =>
            ['ApiError', 'CacheClearCompleteEvent', 'DataImportCompleteEvent'].includes(name)
          )
        );
      }
      if (ts.isIdentifier(node)) {
        const symbol = checker.getSymbolAtLocation(node);
        return (
          symbol?.declarations?.some(
            (declaration) =>
              ts.isVariableDeclaration(declaration) &&
              declaration.initializer &&
              normalized(declaration.initializer, seen)
          ) === true
        );
      }
      if (
        ts.isBinaryExpression(node) &&
        [
          ts.SyntaxKind.EqualsEqualsEqualsToken,
          ts.SyntaxKind.EqualsEqualsToken,
          ts.SyntaxKind.ExclamationEqualsEqualsToken,
          ts.SyntaxKind.ExclamationEqualsToken
        ].includes(node.operatorToken.kind)
      )
        return normalized(node.left, seen) || normalized(node.right, seen);
      return false;
    }
    for (const node of collectNodes(source, () => true)) {
      if (ts.isCallExpression(node) && normalized(node)) roots++;
      if (
        ts.isBinaryExpression(node) &&
        [ts.SyntaxKind.BarBarToken, ts.SyntaxKind.QuestionQuestionToken].includes(
          node.operatorToken.kind
        ) &&
        normalized(node.left)
      )
        violations.push(
          `${location(source, node)}: competing alternative after a required message`
        );
      if (ts.isConditionalExpression(node) && normalized(node.condition))
        violations.push(
          `${location(source, node)}: conditional alternative after a required message`
        );
      if (
        ts.isIfStatement(node) &&
        normalized(node.expression) &&
        collectNodes(
          node.thenStatement,
          (child) =>
            ts.isBinaryExpression(child) &&
            child.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
            normalized(child.left)
        ).length
      )
        violations.push(
          `${location(source, node)}: empty-value reassignment after a required message`
        );
    }
  }
  return { violations: [...new Set(violations)], roots };
}

test('normalized and required messages are consumed without competing alternatives', () => {
  const result = checkMessages(csharpSources);
  assert.ok(result.roots > 0, 'No normalized message roots were resolved');
  console.log(`Resolved normalized message calls: ${result.roots}`);
  assertClean(result.violations);
});

function objectMap(source, name) {
  const declarations = collectNodes(
    source,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
  );
  assert.equal(declarations.length, 1, `Expected one ${name} map`);
  let node = declarations[0].initializer;
  while (ts.isAsExpression(node) || ts.isSatisfiesExpression(node)) node = node.expression;
  assert.ok(ts.isObjectLiteralExpression(node), `${name} is not an object map`);
  return Object.fromEntries(
    node.properties.map((property) => {
      assert.ok(ts.isPropertyAssignment(property), `${name} has an unresolved entry`);
      const key = property.name.getText(source).replace(/^['"]|['"]$/g, '');
      assert.ok(
        ts.isStringLiteral(property.initializer) ||
          property.initializer.kind === ts.SyntaxKind.NullKeyword
      );
      return [
        key,
        property.initializer.kind === ts.SyntaxKind.NullKeyword ? null : property.initializer.text
      ];
    })
  );
}

export function checkReasonSets(backend, frontend, map) {
  const sorted = (values) => [...new Set(values)].sort();
  const expected = JSON.stringify(sorted(backend));
  return [JSON.stringify(sorted(frontend)), JSON.stringify(sorted(Object.keys(map)))].every(
    (value) => value === expected
  )
    ? []
    : ['Closed reason domain, frontend type and presentation map differ'];
}

function wireReasons(entries) {
  const reasons = new Set();
  const violations = [];
  for (const { file, text } of entries.filter(({ file }) => file.endsWith('.cs'))) {
    const words = tokens(text, file);
    for (let index = 0; index < words.length; index++) {
      if (
        words[index].text === 'Refuse' &&
        words[index + 1]?.text === '(' &&
        words[index + 2]?.value !== undefined
      )
        reasons.add(words[index + 2].value);
      if (
        !['GetIntegrationLoginAvailability', 'GetIntegrationLoginReason'].includes(
          words[index].text
        ) ||
        words[index + 1]?.text !== '('
      )
        continue;
      const start = words[index + 1].pair + 1;
      if (!['{', '=>'].includes(words[start]?.text)) continue;
      let end = words[start].pair;
      if (words[start].text === '=>') {
        end = start + 1;
        while (end < words.length && words[end].text !== ';') end++;
      }
      for (let token = start + 1; token < end; token++) {
        if (
          words[index].text === 'GetIntegrationLoginReason' &&
          words[token].text === 'return' &&
          words[token + 1]?.value !== undefined
        )
          reasons.add(words[token + 1].value);
        if (words[token].text !== 'new') continue;
        const opening = token + (words[token + 1]?.text === 'IntegrationLoginAvailability' ? 2 : 1);
        if (words[opening]?.text !== '(' || !['true', 'false'].includes(words[opening + 1]?.text))
          continue;
        const argumentsList = [[]];
        for (let argument = opening + 1; argument < words[opening].pair; argument++) {
          if (words[argument].text === ',') argumentsList.push([]);
          else argumentsList.at(-1).push(words[argument]);
        }
        if (argumentsList.length !== 3) continue;
        const reason = argumentsList[2][0];
        if (reason?.value !== undefined) reasons.add(reason.value);
        if (
          words[opening + 1].text === 'false' &&
          (reason?.text === 'null' || reason?.value === '')
        )
          violations.push(
            `${normalizePath(file)}:${words[token].line}: unavailable integration has no reason`
          );
      }
    }
  }
  return { reasons, violations };
}

test('closed reason domains match their wire types, presentation maps and both locales', () => {
  const findSource = (suffix) => {
    const matches = csharpSources.filter(({ file }) => normalizePath(file).endsWith(suffix));
    assert.equal(matches.length, 1, `Expected one source for ${suffix}`);
    return matches[0];
  };
  const types = findSource('Web/src/types.ts');
  const typeSource = parseSource(types.file, types.text);
  const integration = objectMap(typeSource, 'integrationReasonKeys');
  const lease = findSource('/Auth/IntegrationLease.cs');
  const arms = [...lease.text.matchAll(/"([a-z-]+)"\s*=>\s*"(errors\.integration\.[^"]+)"/g)];
  assert.ok(arms.length > 0 && integration['not-supported']);
  const wire = wireReasons(csharpSources);
  assert.ok(wire.reasons.size > 0);
  assertClean(wire.violations);
  for (const reason of wire.reasons)
    assert.ok(Object.hasOwn(integration, reason), `Unmapped emitted integration reason ${reason}`);
  const domain = collectNodes(
    typeSource,
    (node) => ts.isTypeAliasDeclaration(node) && node.name.text === 'IntegrationReason'
  );
  assert.equal(domain.length, 1);
  assert.equal(domain[0].type.getText(typeSource), 'keyof typeof integrationReasonKeys');
  assertClean(
    checkReasonSets(
      arms.map((arm) => arm[1]),
      Object.keys(integration),
      integration
    )
  );
  assert.deepEqual(Object.fromEntries(arms.map((arm) => [arm[1], arm[2]])), integration);
  assertClean(checkLocales(Object.values(integration), [en, zh]));
  const client = findSource('/Clients/ClientHostnameResponses.cs');
  const api = findSource('Web/src/services/api.service.ts');
  const apiSource = parseSource(api.file, api.text);
  for (const [name, suffix, mapName] of [
    ['ClientHostnamesReason', '/utils/clientHostnameReason.ts', 'reasonTranslationKeys'],
    ['ClientAddressLookupReason', '/ClientGroupModal.tsx', 'lookupReasonKeys']
  ]) {
    const body = client.text.match(new RegExp(`enum ${name}\\s*\\{([^}]+)\\}`));
    assert.ok(body, `Missing ${name}`);
    const backend = tokens(body[1])
      .filter((word) => /^[A-Z]\w*$/.test(word.text))
      .map((word) => word.text[0].toLowerCase() + word.text.slice(1));
    assert.match(client.text, /JsonNamingPolicy\.CamelCase/);
    const declarations = collectNodes(
      apiSource,
      (node) => ts.isTypeAliasDeclaration(node) && node.name.text === name
    );
    assert.equal(declarations.length, 1);
    assert.ok(ts.isUnionTypeNode(declarations[0].type));
    const frontend = declarations[0].type.types.map((node) => {
      assert.ok(ts.isLiteralTypeNode(node) && ts.isStringLiteral(node.literal));
      return node.literal.text;
    });
    const presentation = findSource(suffix);
    const map = objectMap(parseSource(presentation.file, presentation.text), mapName);
    assertClean(checkReasonSets(backend, frontend, map));
    assertClean(
      checkLocales(
        Object.values(map).filter((key) => key !== null),
        [en, zh]
      )
    );
    assert.equal(map.none, null);
    if (name === 'ClientHostnamesReason') assert.equal(map.noClients, null);
  }
});

test('message and reason controls reject missing leaves, mismatched placeholders and domain drift', () => {
  for (const bundle of [{}, { errors: { refused: {} } }, { errors: { refused: 'No {{other}}' } }])
    assert.ok(
      checkLocales(['errors.refused'], [{ errors: { refused: 'No {{name}}' } }, bundle]).length > 0
    );
  assertClean(
    checkLocales(
      ['errors.refused'],
      [{ errors: { refused: 'No {{name}}' } }, { errors: { refused: '否 {{name}}' } }]
    )
  );
  for (const map of [{ valid: 'errors.refused', invented: 'errors.refused' }, {}])
    assert.ok(checkReasonSets(['valid'], ['valid'], map).length > 0);
  assert.ok(
    checkReasonSets(['valid'], ['valid', 'invented'], { valid: 'errors.refused' }).length > 0
  );
});

test('unavailable wire reasons cannot be missing or bypass their presentation domain', () => {
  for (const reason of ['null', '""']) {
    const result = wireReasons([
      {
        file: 'Availability.cs',
        text: `IntegrationLoginAvailability GetIntegrationLoginAvailability() => new(false, null, ${reason});`
      }
    ]);
    assert.ok(result.violations.length > 0);
  }
  const result = wireReasons([
    {
      file: 'Availability.cs',
      text: 'IntegrationLoginAvailability GetIntegrationLoginAvailability() => new(false, null, "new-reason");'
    }
  ]);
  assert.ok(checkReasonSets(result.reasons, ['known'], { known: 'errors.known' }).length > 0);
  assertClean(
    wireReasons([
      {
        file: 'Availability.cs',
        text: 'IntegrationLoginAvailability GetIntegrationLoginAvailability() => new(true, "account", null);'
      }
    ]).violations
  );
});

test('stage keys resolve scoped constants, branches and title keys with comments excluded', () => {
  for (const newline of ['\n', '\r\n']) {
    const result = readEmissions([
      {
        file: 'first.cs',
        text: [
          'class First { const string Key = "errors.first";',
          'object Send() => new Reply { StageKey = Key }; }'
        ].join(newline)
      },
      {
        file: 'second.cs',
        text: [
          'class Second { const string Key = "errors.second";',
          'object Send() => new Reply { TitleStageKey = Key, StageKey = flag ? "signalr.started" : "signalr.finished" }; }',
          '// StageKey = "errors.comment";'
        ].join(newline)
      }
    ]);
    assertClean(result.violations);
    assert.deepEqual([...result.keys.keys()].sort(), [
      'errors.first',
      'errors.second',
      'signalr.finished',
      'signalr.started'
    ]);
    assert.ok(
      readEmissions([{ file: 'invalid.cs', text: 'new Reply { StageKey = Missing.Key };' }])
        .violations.length > 0
    );
  }
});

test('prefix contracts expand into emitted leaves instead of namespace strings', () => {
  const result = readEmissions([
    {
      file: 'scheduled.cs',
      text: 'class Scheduled { const string StageBase = "signalr.maintenance"; object Send() => new Reply { StageKey = $"{StageBase}.complete" }; }'
    },
    {
      file: path.join(repository, 'Web/src/prefix-control.ts'),
      text: [
        'interface Options { i18nBase: string }',
        'function build(options: Options) { const { i18nBase } = options; return { stageKey: `${i18nBase}.starting` }; }',
        'build({ i18nBase: "signalr.maintenance" });'
      ].join('\n')
    }
  ]);
  assertClean(result.violations);
  assert.deepEqual([...result.keys.keys()].sort(), [
    'signalr.maintenance.complete',
    'signalr.maintenance.starting'
  ]);
  assert.ok(
    readEmissions([
      { file: 'unresolved.cs', text: 'new Reply { StageKey = $"{Missing}.complete" };' }
    ]).violations.length > 0
  );
  assert.ok(
    readEmissions([
      {
        file: path.join(repository, 'Web/src/prefix-control.ts'),
        text: 'const prefix = "signalr.maintenance"; const stageKey = `${prefix}.${unknownSuffix}`;'
      }
    ]).violations.length > 0
  );
});

const messagePrelude =
  "import { getErrorMessage as explain } from '@utils/error'; declare const error: unknown;";
const alternatives = { or: ['|', '|'].join(''), coalesce: ['?', '?'].join('') };
for (const [name, fragments] of [
  ['direct', ['show(explain(error)', alternatives.or, '"Another sentence");']],
  [
    'alias',
    ['const message = explain(error); show(message', alternatives.coalesce, '"Another sentence");']
  ],
  [
    'conditional',
    ['const message = explain(error); show(message ? message : "Another sentence");']
  ],
  ['reassignment', ['let message = explain(error); if (!message) message = "Another sentence";']],
  [
    'typed error',
    [
      "import { ApiError } from '@services/apiError'; function show(error: ApiError) { return error.message",
      alternatives.or,
      '"Another sentence"; }'
    ]
  ],
  [
    'required event',
    [
      "import type { CacheClearCompleteEvent } from '@contexts/SignalRContext/types'; function show(event: CacheClearCompleteEvent) { return event.message",
      alternatives.coalesce,
      '"Another sentence"; }'
    ]
  ],
  ...['getCancelledMessage', 'getFailureMessage', 'getSuccessMessage'].map((name) => [
    name,
    [
      "import type { RegistryCompleteConfig } from '@contexts/notifications/types'; declare const config: RegistryCompleteConfig<{}>; show(config.",
      name,
      '?.({}) ',
      alternatives.coalesce,
      ' "Another sentence");'
    ]
  ]),
  [
    'getCompletedMessage',
    [
      "import type { RegistryProgressConfig } from '@contexts/notifications/types'; declare const config: RegistryProgressConfig<{}>; show(config.getCompletedMessage?.({}) ",
      alternatives.coalesce,
      ' "Another sentence");'
    ]
  ],
  [
    'classified alias',
    [
      'const message = explain(error); show(message.includes("read-only") ? t("errors.readOnly") : message ',
      alternatives.or,
      ' "Another sentence");'
    ]
  ]
])
  test(`rejects a competing ${name} message`, () => {
    const result = checkMessages([
      {
        file: path.join(repository, 'Web/src/message-control.ts'),
        text: messagePrelude + fragments.join('')
      }
    ]);
    assert.ok(result.violations.length > 0, `Accepted ${name}`);
  });

for (const [name, text] of [
  [
    'raw optional boundary',
    [
      'function classify(reply: { message?: string }) { return reply.message ',
      alternatives.or,
      ' "Request refused"; }'
    ].join('')
  ],
  [
    'optional detail',
    'function detail(reply: { detail?: string }) { return reply.detail ?? undefined; }'
  ],
  [
    'same sentence localization',
    'const message = explain(error); show(t("errors.key", { defaultValue: message }));'
  ],
  [
    'read-only classification',
    'const message = explain(error); show(message.includes("read-only") ? t("errors.readOnly") : message);'
  ],
  [
    'diagnostic alias',
    'const detail = explain(error); console.error("Request failed:", detail); setError(detail);'
  ],
  [
    'optional error callback',
    [
      "import type { RegistryProgressConfig } from '@contexts/notifications/types'; declare const config: RegistryProgressConfig<{}>; show(config.getErrorMessage?.({}) ",
      alternatives.coalesce,
      ' "Request failed");'
    ].join('')
  ],
  [
    'callback absence',
    "import type { RegistryCompleteConfig } from '@contexts/notifications/types'; declare const config: RegistryCompleteConfig<{}>; const message = config.getSuccessMessage ? config.getSuccessMessage({}) : 'Completed';"
  ]
])
  test(`permits ${name}`, () =>
    assertClean(
      checkMessages([
        { file: path.join(repository, 'Web/src/message-control.ts'), text: messagePrelude + text }
      ]).violations
    ));

test('the combined package gate propagates rejection before starting deadline checks', () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'contract-command-'));
  const scripts = JSON.parse(readFileSync(webFile('package.json'), 'utf8')).scripts;
  const environment = Object.fromEntries(
    Object.entries(process.env).filter(([name]) => !name.startsWith('NODE_TEST_'))
  );
  try {
    mkdirSync(path.join(directory, 'scripts'));
    writeFileSync(
      path.join(directory, 'package.json'),
      JSON.stringify({ type: 'module', scripts })
    );
    writeFileSync(
      path.join(directory, 'scripts/test-server-stage-keys.mjs'),
      'import test from "node:test"; test("contract rejects", () => { throw new Error("Rejected contract"); });'
    );
    const marker = path.join(directory, 'deadline-ran');
    writeFileSync(
      path.join(directory, 'scripts/test-deadline-contract.mjs'),
      `import { writeFileSync } from 'node:fs'; writeFileSync(${JSON.stringify(marker)}, 'yes');`
    );
    writeFileSync(path.join(directory, 'scripts/test-login-deadline.mjs'), '');
    const rejected = spawnSync('npm', ['run', 'validate:contracts'], {
      cwd: directory,
      encoding: 'utf8',
      env: environment,
      shell: process.platform === 'win32'
    });
    assert.notEqual(rejected.status, 0, rejected.stderr + rejected.stdout);
    assert.match(rejected.stdout, /validate:message-contracts/);
    assert.equal(existsSync(marker), false, 'Deadline command ran after a rejected message gate');
    writeFileSync(path.join(directory, 'scripts/test-server-stage-keys.mjs'), '');
    const accepted = spawnSync('npm', ['run', 'validate:contracts'], {
      cwd: directory,
      encoding: 'utf8',
      env: environment,
      shell: process.platform === 'win32'
    });
    assert.equal(accepted.status, 0, accepted.stderr + accepted.stdout);
    assert.equal(readFileSync(marker, 'utf8'), 'yes');
  } finally {
    assert.ok(path.resolve(directory).startsWith(path.resolve(os.tmpdir()) + path.sep));
    rmSync(directory, { recursive: true, force: true });
  }
});

test('a rejected message contract makes the real process fail', () => {
  const result = spawnSync(process.execPath, [fileURLToPath(import.meta.url), '--reject-control'], {
    encoding: 'utf8'
  });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr + result.stdout, /missing string errors.missing/);
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
const shownFor = (data, bundle, recovering = false) => {
  const t = translator(bundle);
  const sentence = evaluate(sentenceExpression, { data, t, recovering });
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

test('the same form names the reset when it is resetting rather than creating', () => {
  // One form serves both jobs, so the fallback wording is the only thing telling the operator
  // which one just failed.
  assert.equal(shownFor({}, zh, true), zh.initialization.adminAccount.errors.recoverFailed);
  assert.equal(shownFor({}, en, true), en.initialization.adminAccount.errors.recoverFailed);
});
