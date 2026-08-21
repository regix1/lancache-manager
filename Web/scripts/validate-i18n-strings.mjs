#!/usr/bin/env node
/**
 * validate-i18n-strings.mjs
 *
 * Fails on a user-facing string written into the source instead of being looked up with
 * t(). validate-i18n-keys.mjs is the other half of this: it checks that every key handed
 * to t() resolves in both locales, which says nothing about a sentence that never reaches
 * t() at all. This one covers that gap.
 *
 * Three shapes are checked, because they are the ones that reach a screen without any
 * other layer getting a say:
 *   - text written between JSX tags
 *   - a literal given to a prop that a component renders as words (VISIBLE_PROPS)
 *   - a literal given to a call that surfaces it (MESSAGE_ARGUMENT)
 *
 * Everything else a string can be is left alone, because none of it is read as words:
 * class names, ids, API paths, storage keys, event names, console output, HTML and ARIA
 * enum values, and the union members and switch discriminants that only ever get
 * compared. Three narrower exclusions are worth naming:
 *
 *   - Text inside <code>, <pre>, <kbd> or any element whose className says font-mono is a
 *     command, a config file or a query. It is copied and pasted, not read, and
 *     translating it would break it.
 *   - A single run with no spaces around path or identifier punctuation is a token, not
 *     prose: a file name, a metric, an environment variable, a connection string.
 *   - A string made only of product names, units and digits translates to itself.
 *
 * Usage:
 *   node scripts/validate-i18n-strings.mjs
 *
 * Exit codes: 0 = clean, 1 = hardcoded user-facing strings found.
 */

import { readdirSync, readFileSync } from 'fs';
import { join, resolve, dirname, relative } from 'path';
import { fileURLToPath } from 'url';
import ts from 'typescript';

const __dirname = dirname(fileURLToPath(import.meta.url));
const WEB_ROOT = resolve(__dirname, '..');
const SRC_ROOT = join(WEB_ROOT, 'src');

/** Props whose value a component turns into words on screen. */
const VISIBLE_PROPS = new Set([
  'placeholder',
  'title',
  'aria-label',
  'alt',
  'label',
  'heading',
  'subtitle',
  'description',
  'emptyMessage',
  'confirmText',
  'cancelText',
  'tooltip'
]);

/**
 * Calls that put an argument in front of the user, and which argument that is.
 * showToast takes its severity first and its message second.
 */
const MESSAGE_ARGUMENT = new Map([
  ['setError', 0],
  ['notifyError', 0],
  ['notifySuccess', 0],
  ['showToast', 1]
]);

/** Elements whose text is a command or a config sample rather than a sentence. */
const CODE_TAGS = new Set(['code', 'pre', 'kbd', 'samp']);

/** The class that marks code and config text laid out in plain divs. */
const CODE_CLASS = 'font-mono';

/**
 * Words that translate to themselves. A string made only of these, digits and punctuation
 * is not prose. Units and file formats sit here for the same reason product names do.
 */
const SELF_TRANSLATING_WORDS = new Set([
  'steam',
  'epic',
  'xbox',
  'battle',
  'riot',
  'blizzard',
  'wsus',
  'grafana',
  'prometheus',
  'docker',
  'postgres',
  'postgresql',
  'sqlite',
  'nginx',
  'lancache',
  'manager',
  'windows',
  'linux',
  'localhost',
  'json',
  'toml',
  'yaml',
  'csv',
  'url',
  'api',
  'http',
  'https',
  'dns',
  'doh',
  'cdn',
  'css',
  'html',
  'svg',
  'png',
  'utc',
  'gmt',
  'kib',
  'mib',
  'gib',
  'tib',
  'bps'
]);

/** Put this on the line and it is skipped, for a string that is deliberately untranslated. */
const IGNORE_MARKER = 'i18n-exempt';

/**
 * @param {string} value
 * @returns {boolean} true when the string is words a reader would read, rather than a
 *   token, a symbol or a product name
 */
function isProse(value) {
  // Entities are punctuation spelled out; "&middot;" is not the word "middot".
  const text = value.replace(/&[a-z]+;|&#\d+;/gi, ' ').trim();
  if (!/[A-Za-z]/.test(text)) return false;
  // An interpolation on its own, e.g. "{{count}}".
  if (/^\{\{[^}]+\}\}$/.test(text)) return false;
  // Trailing punctuation belongs to the sentence, so "Loading..." stays prose while
  // "access.log" does not.
  const core = text.replace(/[.…!?,:;]+$/, '');
  if (!/\s/.test(core) && /[/\\=_@([.:]/.test(core)) return false;
  const words = core.toLowerCase().match(/[a-z]{3,}/g) ?? [];
  return words.some((word) => !SELF_TRANSLATING_WORDS.has(word));
}

/**
 * @param {string} dir
 * @param {string[]} out
 * @returns {string[]} every .ts and .tsx file under dir, excluding declarations
 */
function listSourceFiles(dir, out = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) listSourceFiles(full, out);
    else if (/\.tsx?$/.test(entry.name) && !entry.name.endsWith('.d.ts')) out.push(full);
  }
  return out;
}

/**
 * @param {ts.Node | undefined} node
 * @returns {string | null} the literal text, or null when the value is computed
 */
function literalText(node) {
  if (!node) return null;
  if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) return node.text;
  // A template with holes still carries its prose in the fixed spans.
  if (ts.isTemplateExpression(node)) {
    const spans = [node.head.text, ...node.templateSpans.map((span) => span.literal.text)];
    const joined = spans.join(' ').trim();
    return joined.length > 0 ? joined : null;
  }
  return null;
}

/**
 * @param {ts.JsxAttributeLike} attribute
 * @returns {string | null} the attribute's literal value, unwrapping a { } expression
 */
function attributeText(attribute) {
  if (!ts.isJsxAttribute(attribute)) return null;
  const initializer = attribute.initializer;
  if (initializer && ts.isJsxExpression(initializer)) return literalText(initializer.expression);
  return literalText(initializer);
}

/**
 * @param {ts.Node} node
 * @returns {boolean} true when the node sits inside an element that holds code or config
 */
function isInCodeContext(node) {
  for (let current = node.parent; current; current = current.parent) {
    if (!ts.isJsxElement(current)) continue;
    const opening = current.openingElement;
    if (CODE_TAGS.has(opening.tagName.getText())) return true;
    for (const attribute of opening.attributes.properties) {
      if (!ts.isJsxAttribute(attribute) || attribute.name.getText() !== 'className') continue;
      if (attributeText(attribute)?.includes(CODE_CLASS)) return true;
    }
  }
  return false;
}

/**
 * notifyError logs a silent call to the console and shows nothing, so its message is not
 * user-facing.
 * @param {ts.CallExpression} node
 * @returns {boolean}
 */
function isSilencedCall(node) {
  return node.arguments.some(
    (argument) =>
      ts.isObjectLiteralExpression(argument) &&
      argument.properties.some(
        (property) =>
          ts.isPropertyAssignment(property) &&
          property.name.getText() === 'silent' &&
          property.initializer.kind === ts.SyntaxKind.TrueKeyword
      )
  );
}

/**
 * @param {ts.CallExpression} node
 * @returns {string} the called name, without any receiver
 */
function calleeName(node) {
  const target = node.expression;
  if (ts.isIdentifier(target)) return target.text;
  if (ts.isPropertyAccessExpression(target)) return target.name.text;
  return '';
}

function main() {
  const files = listSourceFiles(SRC_ROOT).filter((file) => {
    const rel = relative(SRC_ROOT, file).replace(/\\/g, '/');
    return !rel.startsWith('i18n/') && !rel.startsWith('test/');
  });

  /** @type {{ file: string, line: number, kind: string, text: string }[]} */
  const hits = [];

  for (const file of files) {
    const rel = relative(WEB_ROOT, file).replace(/\\/g, '/');
    const text = readFileSync(file, 'utf8');
    const lines = text.split(/\r?\n/);
    const source = ts.createSourceFile(
      file,
      text,
      ts.ScriptTarget.Latest,
      true,
      file.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS
    );

    /**
     * @param {ts.Node} node
     * @param {string} kind
     * @param {string} value
     */
    const report = (node, kind, value) => {
      const line = source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;
      if (lines[line - 1]?.includes(IGNORE_MARKER)) return;
      hits.push({ file: rel, line, kind, text: value.replace(/\s+/g, ' ').trim() });
    };

    /** @param {ts.Node} node */
    const visit = (node) => {
      if (ts.isJsxText(node)) {
        if (isProse(node.text) && !isInCodeContext(node)) report(node, 'jsx text', node.text);
      } else if (ts.isJsxAttribute(node) && VISIBLE_PROPS.has(node.name.getText())) {
        const value = attributeText(node);
        if (value !== null && isProse(value)) report(node, `${node.name.getText()} prop`, value);
      } else if (ts.isCallExpression(node) && MESSAGE_ARGUMENT.has(calleeName(node))) {
        const name = calleeName(node);
        const argument = node.arguments[MESSAGE_ARGUMENT.get(name)];
        const value = literalText(argument);
        if (value !== null && isProse(value) && !isSilencedCall(node)) {
          report(argument, `${name}()`, value);
        }
      }
      ts.forEachChild(node, visit);
    };

    visit(source);
  }

  if (hits.length === 0) {
    console.log(`i18n strings OK: ${files.length} source files, no hardcoded user-facing text.`);
    return 0;
  }

  let currentFile = '';
  for (const hit of hits) {
    if (hit.file !== currentFile) {
      currentFile = hit.file;
      console.error(`\n${currentFile}`);
    }
    console.error(`  ${hit.line}  ${hit.kind}: ${hit.text.slice(0, 90)}`);
  }

  console.error(`\n${hits.length} hardcoded user-facing string(s).`);
  console.error(`Move each one into Web/src/i18n/locales/en.json and zh.json and read it back`);
  console.error(`with t('...'), the way the lines around it already do.`);
  console.error(`A string that is deliberately not translated takes an ${IGNORE_MARKER} comment.`);
  return 1;
}

process.exitCode = main();
