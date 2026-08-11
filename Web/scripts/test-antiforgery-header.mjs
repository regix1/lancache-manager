import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { compileToUrl } from './transpile-module.mjs';

/**
 * Every request that changes something has to carry the antiforgery token, or the server refuses it.
 * The token is read from a cookie, and almost every call gets it for free because it is added inside
 * ApiService.getFetchOptions - but a call that builds its own options object gets nothing, and the
 * failure is a 400 on one screen rather than anything a type checker or a lint rule can see.
 *
 * So this checks two things: that the reader itself works, and that no mutating call in the
 * application is sending its request without the header.
 */

const SRC_DIR = fileURLToPath(new URL('../src', import.meta.url));

const MUTATING_METHODS = ['POST', 'PUT', 'PATCH', 'DELETE'];

const FUNNEL_CALLS = ['getFetchOptions(', 'getJsonFetchOptions('];

function* walkSource(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const full = join(directory, entry.name);

    if (entry.isDirectory()) {
      yield* walkSource(full);
    } else if (entry.name.endsWith('.ts') || entry.name.endsWith('.tsx')) {
      yield full;
    }
  }
}

/**
 * The options argument of every `fetch(...)` call in a file, as source text, with the line it sits on.
 */
function fetchCalls(filePath) {
  const source = readFileSync(filePath, 'utf8');
  const sourceFile = ts.createSourceFile(
    filePath,
    source,
    ts.ScriptTarget.Latest,
    true,
    filePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS
  );

  const found = [];
  const visit = (node) => {
    if (
      ts.isCallExpression(node) &&
      ts.isIdentifier(node.expression) &&
      node.expression.text === 'fetch'
    ) {
      const options = node.arguments[1];
      found.push({
        line: sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1,
        options: options ? options.getText(sourceFile) : ''
      });
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);

  return found;
}

const isMutating = (optionsText) =>
  MUTATING_METHODS.some((method) =>
    new RegExp(`method\\s*:\\s*[^,}]*['"\`]${method}['"\`]`).test(optionsText)
  );

const goesThroughTheFunnel = (optionsText) =>
  FUNNEL_CALLS.some((call) => optionsText.includes(call));

test('the reader returns the header when the cookie is there and nothing when it is not', async () => {
  const moduleUrl = await compileToUrl('../src/utils/antiforgery.ts');
  const { antiforgeryHeaders } = await import(moduleUrl);

  globalThis.document = { cookie: '' };
  assert.deepEqual(antiforgeryHeaders(), {}, 'no cookie yet must send no header at all');

  globalThis.document = { cookie: 'other=1; LancacheManager.Antiforgery=abc123; another=2' };
  assert.deepEqual(antiforgeryHeaders(), { 'X-Antiforgery-Token': 'abc123' });

  // The server percent-encodes cookie values on the way out, so the reader has to undo that or the
  // token it sends back is not the token that was issued.
  globalThis.document = { cookie: 'LancacheManager.Antiforgery=a%2Bb%2Fc%3D' };
  assert.deepEqual(antiforgeryHeaders(), { 'X-Antiforgery-Token': 'a+b/c=' });

  globalThis.document = { cookie: 'LancacheManagerAntiforgeryNot=nope' };
  assert.deepEqual(
    antiforgeryHeaders(),
    {},
    'a cookie whose name merely starts the same is not it'
  );

  delete globalThis.document;
});

test('every mutating fetch either goes through the funnel or sends the header itself', () => {
  const uncovered = [];
  let mutating = 0;

  for (const filePath of walkSource(SRC_DIR)) {
    for (const call of fetchCalls(filePath)) {
      if (goesThroughTheFunnel(call.options)) {
        continue;
      }

      if (!isMutating(call.options)) {
        continue;
      }

      mutating += 1;

      if (!call.options.includes('antiforgeryHeaders(')) {
        uncovered.push(`${filePath.slice(SRC_DIR.length + 1)}:${call.line}`);
      }
    }
  }

  assert.ok(mutating > 0, 'found no hand-built mutating fetch at all, so this proves nothing');
  assert.deepEqual(
    uncovered,
    [],
    `these requests change something and send no antiforgery token, so the server will refuse them: ${uncovered.join(', ')}`
  );
});
