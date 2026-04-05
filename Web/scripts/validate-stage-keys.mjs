#!/usr/bin/env node
/* eslint-env node */
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

const __dirname = dirname(fileURLToPath(import.meta.url));
const strict = process.argv.includes('--strict');

// ── Paths ──────────────────────────────────────────────────────────────────────
const REPO_ROOT = resolve(__dirname, '..', '..');
const EN_JSON_PATH = resolve(__dirname, '..', 'src', 'i18n', 'locales', 'en.json');
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

// ── Load en.json signalr keys ──────────────────────────────────────────────────
let enJson;
try {
  enJson = JSON.parse(readFileSync(EN_JSON_PATH, 'utf8'));
} catch (err) {
  console.error(`ERROR: Could not read en.json: ${err.message}`);
  process.exit(1);
}

const signalrSection = enJson.signalr;
if (!signalrSection || typeof signalrSection !== 'object') {
  console.error('ERROR: en.json is missing top-level "signalr" section.');
  process.exit(1);
}

const definedKeys = collectLeafPaths(signalrSection, 'signalr');
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
        `COLLISION: ${key}  is an object in en.json, not a leaf (referenced in ${files.join(', ')}) — inter-worker key conflict, needs coordination`
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

console.log('\nPASS');
process.exit(0);
