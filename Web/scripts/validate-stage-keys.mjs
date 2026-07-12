#!/usr/bin/env node
/**
 * validate-stage-keys.mjs
 *
 * CI script that cross-checks signalr.* stage keys between en.json and code.
 * - Every signalr.* string literal referenced in code must exist in en.json
 * - Every signalr.* leaf key in en.json should be referenced in code (soft warning unless --strict)
 *
 * Usage:
 *   node scripts/validate-stage-keys.mjs          # exit 0 unless missing keys
 *   node scripts/validate-stage-keys.mjs --strict # exit 1 on any missing OR unused key
 */

import { readFileSync, readdirSync } from 'fs';
import { join, resolve, dirname } from 'path';
import { fileURLToPath } from 'url';
import ts from 'typescript';

const __dirname = dirname(fileURLToPath(import.meta.url));
const strict = process.argv.includes('--strict');

// ── Paths ──────────────────────────────────────────────────────────────────────
const REPO_ROOT = resolve(__dirname, '..', '..');
const EN_JSON_PATH = resolve(__dirname, '..', 'src', 'i18n', 'locales', 'en.json');
const ZH_JSON_PATH = resolve(__dirname, '..', 'src', 'i18n', 'locales', 'zh.json');
const REGISTRY_PATH = resolve(
  __dirname,
  '..',
  'src',
  'contexts',
  'notifications',
  'notificationRegistry.ts'
);
const TITLE_KEYS_PATH = resolve(
  __dirname,
  '..',
  'src',
  'contexts',
  'notifications',
  'notificationTitleKeys.ts'
);
const SRC_PATH = resolve(__dirname, '..', 'src');
const DIST_PATH = resolve(__dirname, '..', 'dist');
const SEARCH_DIRS = [
  resolve(REPO_ROOT, 'rust-processor', 'src'),
  resolve(REPO_ROOT, 'Api', 'LancacheManager'),
  resolve(__dirname, '..', 'src')
];
const FILE_EXTENSIONS = new Set(['.rs', '.cs', '.ts', '.tsx']);

// ── Helpers ────────────────────────────────────────────────────────────────────

/** Recursively walk a directory, yielding file paths. */
function* walkDir(dir) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return; // directory may not exist in all environments
  }
  for (const entry of entries) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      // Skip common noise dirs
      if (
        entry.name === 'node_modules' ||
        entry.name === 'target' ||
        entry.name === 'bin' ||
        entry.name === 'obj' ||
        entry.name === '.git'
      )
        continue;
      yield* walkDir(full);
    } else if (entry.isFile()) {
      const ext = full.slice(full.lastIndexOf('.'));
      if (FILE_EXTENSIONS.has(ext)) yield full;
    }
  }
}

/** Recursively walk every file below a directory. */
function* walkAllFiles(dir) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) yield* walkAllFiles(full);
    else if (entry.isFile()) yield full;
  }
}

/** Walk a JSON object recursively, collecting all leaf-node dot-paths. */
function collectLeafPaths(obj, prefix = '') {
  const result = new Set();
  for (const [key, value] of Object.entries(obj)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
      for (const sub of collectLeafPaths(value, path)) result.add(sub);
    } else {
      result.add(path);
    }
  }
  return result;
}

function collectLeafStrings(obj, prefix = '', result = new Map()) {
  for (const [key, value] of Object.entries(obj)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
      collectLeafStrings(value, path, result);
    } else if (typeof value === 'string') {
      result.set(path, value);
    }
  }
  return result;
}

function interpolationNames(template) {
  return new Set([...template.matchAll(/{{\s*([^},\s]+)[^}]*}}/g)].map((match) => match[1]));
}

function sameSet(left, right) {
  return left.size === right.size && [...left].every((value) => right.has(value));
}

function propertyName(node) {
  if (ts.isIdentifier(node) || ts.isStringLiteral(node)) return node.text;
  return undefined;
}

function getObjectProperty(node, name) {
  return node.properties.find(
    (property) => ts.isPropertyAssignment(property) && propertyName(property.name) === name
  );
}

function stringInitializer(property) {
  return property && ts.isStringLiteral(property.initializer)
    ? property.initializer.text
    : undefined;
}

function containsStageKeyAccess(node) {
  let found = false;
  function visit(child) {
    if (ts.isPropertyAccessExpression(child) && child.name.text === 'stageKey') {
      found = true;
      return;
    }
    ts.forEachChild(child, visit);
  }
  visit(node);
  return found;
}

// ── Load en.json signalr keys ──────────────────────────────────────────────────
let enJson;
let zhJson;
try {
  enJson = JSON.parse(readFileSync(EN_JSON_PATH, 'utf8'));
  zhJson = JSON.parse(readFileSync(ZH_JSON_PATH, 'utf8'));
} catch (err) {
  console.error(`ERROR: Could not read notification locales: ${err.message}`);
  process.exit(1);
}

const signalrSection = enJson.signalr;
if (!signalrSection || typeof signalrSection !== 'object') {
  console.error('ERROR: en.json is missing top-level "signalr" section.');
  process.exit(1);
}

const definedKeys = collectLeafPaths(signalrSection, 'signalr');
const enLeafStrings = collectLeafStrings(enJson);
const zhLeafStrings = collectLeafStrings(zhJson);
console.log(`Defined signalr keys in en.json: ${definedKeys.size}`);

// ── Scan code for signalr.* string literals ────────────────────────────────────
const KEY_PATTERN = /['"`]signalr\.[a-zA-Z0-9._]+['"`]/g;

/** @type {Map<string, string[]>} key -> files that reference it */
const referencedKeys = new Map();

for (const searchDir of SEARCH_DIRS) {
  for (const filePath of walkDir(searchDir)) {
    let content;
    try {
      content = readFileSync(filePath, 'utf8');
    } catch {
      continue;
    }

    const matches = content.match(KEY_PATTERN);
    if (!matches) continue;

    for (const match of matches) {
      // Strip surrounding quotes
      const key = match.slice(1, -1);
      if (!referencedKeys.has(key)) referencedKeys.set(key, []);
      const relPath = filePath.replace(REPO_ROOT + '/', '').replace(REPO_ROOT + '\\', '');
      if (!referencedKeys.get(key).includes(relPath)) {
        referencedKeys.get(key).push(relPath);
      }
    }
  }
}

console.log(`Unique signalr keys referenced in code: ${referencedKeys.size}`);

// ── Helper: check if a dot-path resolves to a non-leaf node (object) in en.json ──
function resolveJsonPath(obj, dotPath) {
  const parts = dotPath.split('.');
  let cur = obj;
  for (const part of parts) {
    if (cur === null || typeof cur !== 'object') return undefined;
    cur = cur[part];
  }
  return cur;
}

// ── Check 1: every referenced key must exist in en.json ───────────────────────
let missingCount = 0;
let collisionCount = 0;
for (const [key, files] of referencedKeys) {
  if (!definedKeys.has(key)) {
    // Check if the path resolves to an object (leaf/object collision between workers)
    const resolved = resolveJsonPath(enJson, key);
    if (resolved !== null && typeof resolved === 'object') {
      console.warn(
        `COLLISION: ${key}  is an object in en.json, not a leaf (referenced in ${files.join(', ')}) - inter-worker key conflict, needs coordination`
      );
      collisionCount++;
    } else {
      console.error(`MISSING: ${key}  (referenced in ${files.join(', ')})`);
      missingCount++;
    }
  }
}

// ── Check 2: every en.json key should be referenced somewhere ─────────────────
let unusedCount = 0;
for (const key of definedKeys) {
  if (!referencedKeys.has(key)) {
    console.warn(`UNUSED:  ${key}`);
    unusedCount++;
  }
}

// ── Summary ───────────────────────────────────────────────────────────────────
console.log(
  `\nSummary: ${definedKeys.size} keys defined, ${referencedKeys.size} referenced in code, ${missingCount} missing, ${collisionCount} collisions (inter-worker), ${unusedCount} unused`
);

if (collisionCount > 0) {
  console.warn(
    `\nNOTE: ${collisionCount} collision(s) found where a key is referenced as a leaf but exists as an object in en.json.`
  );
  console.warn(
    'These require coordination between worker-rust/worker-csharp to use the sub-key variants (e.g., .default, .fatal).'
  );
}

if (missingCount > 0) {
  console.error('\nFAIL: Missing keys must be added to en.json before merging.');
  process.exit(1);
}

if (strict && unusedCount > 0) {
  console.error(
    '\nFAIL (--strict): Unused keys must be removed from en.json or referenced in code.'
  );
  process.exit(1);
}

// ── Check 3: every simple recovery has explicit translation validation ───────
let notificationValidationErrors = 0;
const registrySource = ts.createSourceFile(
  REGISTRY_PATH,
  readFileSync(REGISTRY_PATH, 'utf8'),
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TS
);

let registryArray;
function findRegistry(node) {
  if (
    ts.isVariableDeclaration(node) &&
    node.name.getText(registrySource) === 'NOTIFICATION_REGISTRY' &&
    node.initializer &&
    ts.isArrayLiteralExpression(node.initializer)
  ) {
    registryArray = node.initializer;
  }
  ts.forEachChild(node, findRegistry);
}
findRegistry(registrySource);

if (!registryArray) {
  console.error('RECOVERY: Could not locate NOTIFICATION_REGISTRY array.');
  notificationValidationErrors++;
} else {
  for (const entry of registryArray.elements) {
    if (!ts.isObjectLiteralExpression(entry)) continue;
    const type = stringInitializer(getObjectProperty(entry, 'type')) ?? '<unknown>';
    const recoveryProperty = getObjectProperty(entry, 'recovery');
    if (!recoveryProperty || !ts.isObjectLiteralExpression(recoveryProperty.initializer)) continue;
    const recovery = recoveryProperty.initializer;
    if (stringInitializer(getObjectProperty(recovery, 'kind')) !== 'simple') continue;

    const validationProperty = getObjectProperty(recovery, 'translationValidation');
    if (!validationProperty || !ts.isObjectLiteralExpression(validationProperty.initializer)) {
      console.error(`RECOVERY: ${type} simple recovery is unclassified.`);
      notificationValidationErrors++;
      continue;
    }

    const validation = validationProperty.initializer;
    const validationKind = stringInitializer(getObjectProperty(validation, 'kind'));
    if (validationKind !== 'dedicated' && validationKind !== 'stageKey') {
      console.error(`RECOVERY: ${type} has invalid translation validation kind.`);
      notificationValidationErrors++;
      continue;
    }

    const createNotification = getObjectProperty(recovery, 'createNotification');
    if (createNotification) {
      let rawStageTranslation = false;
      function inspectRawTranslation(node) {
        if (
          ts.isCallExpression(node) &&
          ts.isPropertyAccessExpression(node.expression) &&
          node.expression.expression.getText(registrySource) === 'i18n' &&
          node.expression.name.text === 't' &&
          node.arguments[0] &&
          containsStageKeyAccess(node.arguments[0])
        ) {
          rawStageTranslation = true;
        }
        ts.forEachChild(node, inspectRawTranslation);
      }
      inspectRawTranslation(createNotification.initializer);
      if (rawStageTranslation) {
        console.error(`RECOVERY: ${type} bypasses the safe recovery translator.`);
        notificationValidationErrors++;
      }
    }

    if (validationKind !== 'stageKey') continue;
    const casesProperty = getObjectProperty(validation, 'cases');
    if (!casesProperty || !ts.isArrayLiteralExpression(casesProperty.initializer)) {
      console.error(`RECOVERY: ${type} stageKey validation has no cases.`);
      notificationValidationErrors++;
      continue;
    }

    for (const validationCase of casesProperty.initializer.elements) {
      if (!ts.isObjectLiteralExpression(validationCase)) continue;
      const stageKey = stringInitializer(getObjectProperty(validationCase, 'stageKey'));
      const contextProperty = getObjectProperty(validationCase, 'context');
      const contextKeys = new Set();
      if (contextProperty && ts.isObjectLiteralExpression(contextProperty.initializer)) {
        for (const contextEntry of contextProperty.initializer.properties) {
          const name = propertyName(contextEntry.name);
          if (name) contextKeys.add(name);
        }
      }

      const enTemplate = stageKey ? enLeafStrings.get(stageKey) : undefined;
      const zhTemplate = stageKey ? zhLeafStrings.get(stageKey) : undefined;
      if (!stageKey || enTemplate === undefined || zhTemplate === undefined) {
        console.error(`RECOVERY: ${type} case references missing locale key ${stageKey}.`);
        notificationValidationErrors++;
        continue;
      }

      const enTokens = interpolationNames(enTemplate);
      const zhTokens = interpolationNames(zhTemplate);
      if (!sameSet(enTokens, zhTokens)) {
        console.error(`RECOVERY: ${stageKey} has different en/zh placeholder sets.`);
        notificationValidationErrors++;
      }
      for (const token of enTokens) {
        if (!contextKeys.has(token)) {
          console.error(`RECOVERY: ${type} ${stageKey} context is missing ${token}.`);
          notificationValidationErrors++;
        }
      }
      const simulateTranslation = (template) =>
        template.replace(/{{\s*([^},\s]+)[^}]*}}/g, (match, token) =>
          contextKeys.has(token) ? 'value' : match
        );
      if (
        /{{|}}/.test(simulateTranslation(enTemplate)) ||
        /{{|}}/.test(simulateTranslation(zhTemplate))
      ) {
        console.error(`RECOVERY: ${type} ${stageKey} can render an unresolved token.`);
        notificationValidationErrors++;
      }
    }
  }
}

// Corruption metric fragments are composed separately from recovered stage keys.
for (const [key, enTemplate] of enLeafStrings) {
  if (!key.startsWith('signalr.corruptionDetect.metrics.')) continue;
  const zhTemplate = zhLeafStrings.get(key);
  if (
    zhTemplate === undefined ||
    !sameSet(interpolationNames(enTemplate), interpolationNames(zhTemplate))
  ) {
    console.error(`LOCALE: ${key} is missing or has different en/zh placeholders.`);
    notificationValidationErrors++;
  }
}

// ── Check 4: stable Corruption Scan title in source and fresh dist ───────────
const titleSource = readFileSync(TITLE_KEYS_PATH, 'utf8');
const expectedTitleMapping =
  "corruption_detection: 'common.notifications.titles.corruptionDetection'";
if (!titleSource.includes(expectedTitleMapping)) {
  console.error('TITLE: corruption_detection does not use the stable corruptionDetection key.');
  notificationValidationErrors++;
}
if (enJson.common?.notifications?.titles?.corruptionDetection !== 'Corruption Scan') {
  console.error('TITLE: English corruption detection title is not the required value.');
  notificationValidationErrors++;
}

const obsoleteTitle = ['Corrupt Cache', ' File Scan'].join('');
for (const filePath of walkAllFiles(SRC_PATH)) {
  if (readFileSync(filePath, 'utf8').includes(obsoleteTitle)) {
    console.error(`TITLE: obsolete wording remains in ${filePath}.`);
    notificationValidationErrors++;
  }
}

if (process.argv.includes('--dist')) {
  let foundCurrentTitle = false;
  let foundObsoleteTitle = false;
  for (const filePath of walkAllFiles(DIST_PATH)) {
    const content = readFileSync(filePath, 'utf8');
    foundCurrentTitle ||= content.includes('Corruption Scan');
    foundObsoleteTitle ||= content.includes(obsoleteTitle);
  }
  if (!foundCurrentTitle || foundObsoleteTitle) {
    console.error('TITLE: fresh dist title assertion failed.');
    notificationValidationErrors++;
  }
}

if (notificationValidationErrors > 0) {
  console.error(`\nFAIL: ${notificationValidationErrors} notification validation error(s).`);
  process.exit(1);
}

console.log('\nPASS');
process.exit(0);
