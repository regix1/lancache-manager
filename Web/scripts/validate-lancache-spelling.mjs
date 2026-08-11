#!/usr/bin/env node
/**
 * validate-lancache-spelling.mjs
 *
 * The product name is spelled LANCache. This fails on the three wrong spellings that
 * read as correct at a glance, "Lancache", "LanCache" and "LANcache", across every
 * tracked file so pre-existing text is covered and not only the current diff.
 *
 * Code identifiers are left alone. A name like LancacheManager is a C# type, not a
 * spelling of the product, and renaming those breaks the build. Only a standalone
 * word counts, so the match must not touch a letter, digit or underscore on either
 * side. Four more spellings are load-bearing rather than prose and are listed in
 * ALLOWED_CONTEXTS below.
 *
 * Usage:
 *   node scripts/validate-lancache-spelling.mjs
 *
 * Exit codes: 0 = clean, 1 = wrong spellings found.
 */

import { execFileSync } from 'child_process';
import { readFileSync } from 'fs';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(__dirname, '..', '..');

const CORRECT_SPELLING = 'LANCache';

/** A standalone word only. Adjacent letters, digits or underscores mean it is an identifier. */
const WRONG_SPELLING_PATTERN = /(?<![A-Za-z0-9_])(Lancache|LanCache|LANcache)(?![A-Za-z0-9_])/g;

/**
 * Spellings that are an identifier on a wire or in configuration rather than prose.
 * Rewriting any of these changes behaviour: the header is what the upstream nginx
 * actually sends, the configuration keys are what appsettings and the LanCache__
 * environment variables bind to, and the last one is the upstream project's own name.
 */
const ALLOWED_CONTEXTS = [
  { before: /X-$/, reason: 'X-LanCache-Processed-By response header' },
  { after: /^:[A-Z]/, reason: 'configuration key path, e.g. LanCache:CachePath' },
  { after: /^":/, reason: 'configuration section key in appsettings' },
  { after: /^\.NET/, reason: 'LanCache.NET, the upstream project' }
];

/**
 * The app's display name is "LANCache Manager". Running it together as LancacheManager
 * is right in code and wrong in a sentence, and the published API reference had it in
 * prose. Only the surfaces below are checked for it, never source files: in code
 * "LancacheManager" is a namespace, a path segment in a test, the metrics meter name and
 * the data-protection ApplicationName that session cookies are tied to, and a repo-wide
 * version of this check flags 67 of those against 15 real ones.
 */
const PRODUCT_NAME_RUN_TOGETHER = /(?<![/\\.])LancacheManager(?![A-Za-z0-9_./\\:|;-])/g;

/**
 * "LancacheManager | v1" is the OpenAPI document title. Nothing declares it, so it is
 * the .NET default taken from the assembly name, and the generated reference quotes it.
 * Correcting it means setting an explicit title on the OpenAPI document rather than
 * editing this text, which would be overwritten on the next generation anyway.
 */
const OPENAPI_DOCUMENT_TITLE = /^ \| v/;
const PROSE_PATH_PREFIXES = ['docs-site/content/', 'docs-site/assets/', 'Web/src/i18n/locales/'];

/** Put this on a line whose spelling is deliberate and the line is skipped. */
const IGNORE_MARKER = 'brand-spelling-ok';

const SELF_PATH = 'Web/scripts/validate-lancache-spelling.mjs';

/**
 * @returns {string[]} every tracked file path, repo-root relative
 */
function listTrackedFiles() {
  const output = execFileSync('git', ['ls-files', '-z'], {
    cwd: REPO_ROOT,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024
  });
  return output.split('\0').filter(Boolean);
}

/**
 * @param {string} before text on the line before the match
 * @param {string} after text on the line after the match
 * @returns {string | null} why the spelling is allowed, or null when it is a real hit
 */
function findAllowedContext(before, after) {
  for (const context of ALLOWED_CONTEXTS) {
    if (context.before !== undefined && context.before.test(before)) {
      return context.reason;
    }
    if (context.after !== undefined && context.after.test(after)) {
      return context.reason;
    }
  }
  return null;
}

/**
 * @param {string} filePath repo-root relative
 * @returns {Array<{ line: number, column: number, spelling: string, text: string }>}
 */
function findWrongSpellings(filePath) {
  let content;
  try {
    content = readFileSync(resolve(REPO_ROOT, filePath), 'utf8');
  } catch {
    return [];
  }

  if (content.includes('\0')) {
    return [];
  }

  const hits = [];
  const lines = content.split('\n');
  const isProse = PROSE_PATH_PREFIXES.some((prefix) => filePath.startsWith(prefix));

  for (let index = 0; index < lines.length; index += 1) {
    const text = lines[index];
    if (text.includes(IGNORE_MARKER)) {
      continue;
    }

    WRONG_SPELLING_PATTERN.lastIndex = 0;
    let match = WRONG_SPELLING_PATTERN.exec(text);

    while (match !== null) {
      const before = text.slice(0, match.index);
      const after = text.slice(match.index + match[0].length);

      if (findAllowedContext(before, after) === null) {
        hits.push({
          line: index + 1,
          column: match.index + 1,
          spelling: match[0],
          correction: CORRECT_SPELLING,
          text: text.trim()
        });
      }

      match = WRONG_SPELLING_PATTERN.exec(text);
    }

    if (!isProse) {
      continue;
    }

    PRODUCT_NAME_RUN_TOGETHER.lastIndex = 0;
    let runTogether = PRODUCT_NAME_RUN_TOGETHER.exec(text);

    while (runTogether !== null) {
      const after = text.slice(runTogether.index + runTogether[0].length);

      if (!OPENAPI_DOCUMENT_TITLE.test(after)) {
        hits.push({
          line: index + 1,
          column: runTogether.index + 1,
          spelling: runTogether[0],
          correction: `${CORRECT_SPELLING} Manager`,
          text: text.trim()
        });
      }

      runTogether = PRODUCT_NAME_RUN_TOGETHER.exec(text);
    }
  }

  return hits;
}

function main() {
  const files = listTrackedFiles().filter((filePath) => filePath !== SELF_PATH);
  let total = 0;

  for (const filePath of files) {
    const hits = findWrongSpellings(filePath);
    if (hits.length === 0) {
      continue;
    }

    console.error(`\n${filePath}`);
    for (const hit of hits) {
      total += 1;
      console.error(`  ${hit.line}:${hit.column}  ${hit.spelling} -> ${hit.correction}`);
      console.error(`            ${hit.text.slice(0, 100)}`);
    }
  }

  if (total === 0) {
    console.log(
      `Spelling OK: every product name reads ${CORRECT_SPELLING} outside identifiers and config keys.`
    );
    return 0;
  }

  console.error(`\n${total} wrong spelling(s). The product name is ${CORRECT_SPELLING}.`);
  console.error(`Fix each line above by hand. Do NOT run a find-and-replace over the repo:`);
  console.error(`identifiers, the X-LanCache-Processed-By header and the LanCache: config keys`);
  console.error(`must keep their current spelling, and a bulk substitution will break them.`);
  console.error(
    `A line whose spelling is deliberate can be skipped with a ${IGNORE_MARKER} comment.`
  );
  return 1;
}

process.exitCode = main();
