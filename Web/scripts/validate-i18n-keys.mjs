#!/usr/bin/env node
/**
 * validate-i18n-keys.mjs
 *
 * CI script that checks every translation key passed to t() resolves in BOTH
 * en.json and zh.json.
 *
 * Why resolution and not "no inline fallback": a string second argument such as
 *   t('management.cache.cachedScan', 'Cached scan')
 * suppresses i18next's missing-key warning, so a key that exists in no locale at
 * all still renders perfectly in English. tsc, eslint, knip and vite all pass and
 * the page is only ever wrong for a reader of another language. Passing a fallback
 * is legitimate on its own; the defect is the key being absent. This checks the
 * key, which leaves the many correct fallbacks alone.
 *
 * A key counts as resolved when its path exists in a locale. The value may be a
 * string, an array (keys read with { returnObjects: true }) or an object. A key
 * whose plural partner exists (key_one / key_other) counts as resolved too,
 * because that is how i18next stores a counted string.
 *
 * Call sites are read from the TypeScript AST, not from text, so a key that is
 * assembled rather than written inline is still checked:
 *
 *   static        t('a.b.c')
 *   ternary       t(flag ? 'a.b.c' : 'a.b.d')      both branches checked
 *   constant      t(SOME_KEY), t(KEYS.BLOCKED)     traced through the declaration,
 *                                                  across files and through chains
 *   lookup table  t(LABEL_KEYS[range])             every value in the table checked
 *   helper call   t(labelKeyFor(method))           every key the helper can return
 *   prefixed      t('a.b.' + x), t(`a.b.${x}`)     the static parent must be an object
 *   runtime       t(event.stageKey)                nothing static to trace
 *
 * A key does not have to reach a t() call to be checked. Most keys in this app
 * are declared as data - `{ labelKey: 'management.x.label' }` on a row, a tab, a
 * dropdown option - and only ever reach t() through a variable whose value is
 * decided at runtime. Those literals are checked where they are written, which is
 * what closes the gap that call-site scanning alone leaves open. A string counts
 * as a key declaration when it is dotted, its first segment is a section that
 * exists in en.json, and its name is not on the NOT_TRANSLATION_KEYS list.
 *
 * What is left over is a key the server supplies at runtime - event.stageKey and
 * its relatives. Those are signalr.* keys and are covered by validate-stage-keys.mjs,
 * which checks the literals on the sending side in Rust and C#. They are counted
 * and reported here, never failed. Run with --list-unresolved to see them.
 *
 * Two caps keep a pathological chain or a very wide lookup table from running
 * away. When either one stops the analysis short, the keys behind it were never
 * looked up, so the run reports the site and fails instead of printing a clean
 * result for an analysis it did not finish.
 *
 * Usage:
 *   node scripts/validate-i18n-keys.mjs
 *   node scripts/validate-i18n-keys.mjs --list-unresolved
 *   node scripts/validate-i18n-keys.mjs --src <dir>
 */

import { readFileSync, readdirSync, existsSync } from 'fs';
import { join, resolve, dirname, relative, sep } from 'path';
import { fileURLToPath } from 'url';
import ts from 'typescript';

const __dirname = dirname(fileURLToPath(import.meta.url));
const WEB_DIR = resolve(__dirname, '..');
const SRC_DIR = join(WEB_DIR, 'src');
const LOCALES_DIR = join(SRC_DIR, 'i18n', 'locales');
const LOCALES = ['en.json', 'zh.json'];

/** The tree to read call sites from. Locales are always the ones in src. */
function scanDir() {
  const flag = process.argv.indexOf('--src');
  const value = flag === -1 ? undefined : process.argv[flag + 1];
  return value ? resolve(value) : SRC_DIR;
}

const SCAN_DIR = scanDir();

/** Import aliases, mirroring resolve.alias in vite.config.ts. Longest first. */
const ALIASES = [
  ['@components', join(SRC_DIR, 'components')],
  ['@services', join(SRC_DIR, 'services')],
  ['@contexts', join(SRC_DIR, 'contexts')],
  ['@hooks', join(SRC_DIR, 'hooks')],
  ['@utils', join(SRC_DIR, 'utils')],
  ['@', SRC_DIR]
];

const KEY_SHAPE = /^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$/;
const DOTTED_KEY_SHAPE = /^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)+$/;

/** Names that end in Key/Keys but hold something other than a translation key. */
const NOT_TRANSLATION_KEYS = new Set([
  'apiKey',
  'cacheKey',
  'storageKey',
  'sessionKey',
  'queryKey',
  'rowKey',
  'sortKey',
  'itemKey',
  'dataKey',
  'stateKey',
  'licenseKey',
  'publicKey',
  'privateKey',
  'secretKey'
]);

/** Keep a pathological chain or a wide lookup table from running away. */
const MAX_DEPTH = 12;
const MAX_CANDIDATES = 200;

/**
 * Where a cap stopped the analysis short. A candidate that is dropped here is a
 * key nobody ever looks up, so the run has to say so rather than pass.
 */
const cappedSites = [];
const cappedSeen = new Set();

function siteOf(node) {
  const source = node.getSourceFile();
  return {
    file: relative(SCAN_DIR, source.fileName).split(sep).join('/'),
    line: source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1
  };
}

/** The one place a cap is recorded, so no cap can drop candidates in silence. */
function noteCapped(node, cause, note) {
  const where = siteOf(node);
  const id = `${where.file}:${where.line}:${cause}`;
  if (cappedSeen.has(id)) return;
  cappedSeen.add(id);
  cappedSites.push({ ...where, cause, note });
}

function capCandidates(candidates, node, cause) {
  if (candidates.length <= MAX_CANDIDATES) return candidates;
  const dropped = candidates.length - MAX_CANDIDATES;
  noteCapped(
    node,
    cause,
    `${candidates.length} candidates, ${MAX_CANDIDATES} checked, ${dropped} dropped`
  );
  return candidates.slice(0, MAX_CANDIDATES);
}

// ── Locales ───────────────────────────────────────────────────────────────────

function loadLocale(name) {
  return JSON.parse(readFileSync(join(LOCALES_DIR, name), 'utf8'));
}

function lookup(tree, key) {
  return key.split('.').reduce((node, part) => {
    if (node === undefined || node === null || typeof node !== 'object') return undefined;
    return node[part];
  }, tree);
}

function resolvesIn(tree, key) {
  if (lookup(tree, key) !== undefined) return true;
  // A counted string is stored under its plural forms, not under the bare key.
  return lookup(tree, `${key}_other`) !== undefined || lookup(tree, `${key}_one`) !== undefined;
}

function isObjectIn(tree, key) {
  const node = lookup(tree, key);
  return typeof node === 'object' && node !== null && !Array.isArray(node);
}

const locales = Object.fromEntries(LOCALES.map((name) => [name, loadLocale(name)]));
const SECTIONS = new Set(Object.keys(locales['en.json']));

// ── Parsing ───────────────────────────────────────────────────────────────────

function* walkDir(dir) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'node_modules' || entry.name === '.git') continue;
      if (full === LOCALES_DIR) continue;
      yield* walkDir(full);
    } else if (entry.isFile() && (entry.name.endsWith('.ts') || entry.name.endsWith('.tsx'))) {
      yield full;
    }
  }
}

/** @type {Map<string, ts.SourceFile | null>} */
const sourceCache = new Map();

function parseFile(file) {
  if (sourceCache.has(file)) return sourceCache.get(file);
  let text;
  try {
    text = readFileSync(file, 'utf8');
  } catch {
    sourceCache.set(file, null);
    return null;
  }
  const source = ts.createSourceFile(
    file,
    text,
    ts.ScriptTarget.Latest,
    true,
    file.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS
  );
  sourceCache.set(file, source);
  return source;
}

/** Resolve an import specifier to a file on disk, or undefined for a package. */
function resolveImport(specifier, fromFile) {
  let base;
  if (specifier.startsWith('.')) {
    base = resolve(dirname(fromFile), specifier);
  } else {
    const alias = ALIASES.find(([name]) => specifier === name || specifier.startsWith(`${name}/`));
    if (!alias) return undefined;
    base = join(alias[1], specifier.slice(alias[0].length));
  }
  const candidates = [`${base}.ts`, `${base}.tsx`, join(base, 'index.ts'), join(base, 'index.tsx')];
  return candidates.find((candidate) => existsSync(candidate));
}

/** Strip the wrappers that sit between a declaration and its value. */
function unwrap(node) {
  let current = node;
  while (
    current &&
    (ts.isParenthesizedExpression(current) ||
      ts.isAsExpression(current) ||
      ts.isSatisfiesExpression(current) ||
      ts.isNonNullExpression(current) ||
      ts.isTypeAssertionExpression(current))
  ) {
    current = current.expression;
  }
  return current;
}

function literalText(node) {
  const value = unwrap(node);
  if (!value) return undefined;
  if (ts.isStringLiteral(value) || ts.isNoSubstitutionTemplateLiteral(value)) return value.text;
  return undefined;
}

function propertyName(node) {
  if (!node) return undefined;
  if (ts.isIdentifier(node) || ts.isStringLiteral(node)) return node.text;
  return undefined;
}

/** Which file each imported name came from. */
function collectImports(source) {
  /** @type {Map<string, {file: string, name: string}>} */
  const imports = new Map();
  for (const statement of source.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier))
      continue;
    const target = resolveImport(statement.moduleSpecifier.text, source.fileName);
    const bindings = statement.importClause?.namedBindings;
    if (!target || !bindings || !ts.isNamedImports(bindings)) continue;
    for (const element of bindings.elements) {
      imports.set(element.name.text, {
        file: target,
        name: (element.propertyName ?? element.name).text
      });
    }
  }
  return imports;
}

/** @type {Map<string, Map<string, {file: string, name: string}>>} */
const importCache = new Map();

function importsFor(file) {
  const cached = importCache.get(file);
  if (cached) return cached;
  const source = parseFile(file);
  const imports = source ? collectImports(source) : new Map();
  importCache.set(file, imports);
  return imports;
}

/** The top-level value a file exports under a name. */
function findExported(file, name) {
  const source = parseFile(file);
  if (!source) return undefined;
  for (const statement of source.statements) {
    if (ts.isVariableStatement(statement)) {
      for (const declaration of statement.declarationList.declarations) {
        if (ts.isIdentifier(declaration.name) && declaration.name.text === name)
          return declaration.initializer;
      }
    } else if (ts.isFunctionDeclaration(statement) && statement.name?.text === name) {
      return statement;
    }
  }
  return undefined;
}

/** The statements a node holds, when it is something that can hold declarations. */
function scopeStatements(node) {
  if (ts.isSourceFile(node) || ts.isBlock(node) || ts.isModuleBlock(node)) return node.statements;
  if (ts.isCaseClause(node) || ts.isDefaultClause(node)) return node.statements;
  return undefined;
}

/**
 * What a name holds where it is used. Walks out through the enclosing scopes
 * first, so a `const baseKey` inside one component is not confused with a
 * different `const baseKey` in the next one, then falls back to the import that
 * brought the name into the file.
 *
 * Returns undefined when the name is a parameter, a destructured binding or
 * anything else whose value only exists at runtime.
 */
function findNamed(fromNode, name, seen) {
  const source = fromNode.getSourceFile();
  const id = `${source.fileName}::${name}::${fromNode.pos}`;
  if (seen.has(id)) return undefined;
  seen.add(id);

  for (let scope = fromNode; scope; scope = scope.parent) {
    const statements = scopeStatements(scope);
    if (statements) {
      for (const statement of statements) {
        if (ts.isVariableStatement(statement)) {
          for (const declaration of statement.declarationList.declarations) {
            if (ts.isIdentifier(declaration.name) && declaration.name.text === name) {
              return declaration.initializer;
            }
            // `const { labelKey } = row` - the value comes from the object.
            if (!ts.isIdentifier(declaration.name) && bindsName(declaration.name, name)) {
              return undefined;
            }
          }
        } else if (ts.isFunctionDeclaration(statement) && statement.name?.text === name) {
          return statement;
        }
      }
    }
    // A parameter of this name shadows anything further out.
    if (isFunctionNode(scope) && scope.parameters) {
      for (const parameter of scope.parameters) {
        if (ts.isIdentifier(parameter.name) && parameter.name.text === name) return undefined;
        if (!ts.isIdentifier(parameter.name) && bindsName(parameter.name, name)) return undefined;
      }
    }
  }

  const imported = importsFor(source.fileName).get(name);
  if (!imported) return undefined;
  const target = findExported(imported.file, imported.name);
  return target;
}

/** Does a destructuring pattern bind this name? */
function bindsName(pattern, name) {
  let found = false;
  const visit = (node) => {
    if (found) return;
    if (ts.isBindingElement(node) && ts.isIdentifier(node.name) && node.name.text === name) {
      found = true;
      return;
    }
    ts.forEachChild(node, visit);
  };
  visit(pattern);
  return found;
}

// ── Reading a key out of an expression ────────────────────────────────────────

const STATIC = (key) => ({ kind: 'static', key });
const PREFIXED = (head) => ({ kind: 'prefixed', head });
const UNRESOLVED = [{ kind: 'unresolved' }];

function isFunctionNode(node) {
  return (
    node &&
    (ts.isArrowFunction(node) || ts.isFunctionExpression(node) || ts.isFunctionDeclaration(node))
  );
}

/** Every expression a function can return. Undefined when a return is opaque. */
function returnExpressions(fn) {
  const body = fn.body;
  if (!body) return undefined;
  if (!ts.isBlock(body)) return [body];

  const found = [];
  let understood = true;
  const visit = (node) => {
    if (!understood) return;
    // A nested function has its own returns; they are not this function's.
    if (isFunctionNode(node)) return;
    if (ts.isReturnStatement(node)) {
      if (!node.expression) {
        understood = false;
        return;
      }
      found.push(node.expression);
      return;
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(body, visit);
  return understood && found.length > 0 ? found : undefined;
}

/** Every string value an object or array literal holds, by name and in order. */
function tableEntries(node) {
  if (ts.isObjectLiteralExpression(node)) {
    const byName = new Map();
    const values = [];
    for (const property of node.properties) {
      if (!ts.isPropertyAssignment(property)) continue;
      values.push(property.initializer);
      const name = propertyName(property.name);
      if (name) byName.set(name, property.initializer);
    }
    return { byName, values };
  }
  if (ts.isArrayLiteralExpression(node)) {
    return { byName: new Map(), values: [...node.elements] };
  }
  return undefined;
}

/**
 * Every key an expression can evaluate to, as a list of findings:
 *   static     the whole key is known
 *   prefixed   only the leading literal is known; the tail is built at runtime
 *   unresolved nothing static to go on
 */
function keysFrom(expression, seen = new Set(), depth = 0) {
  if (depth > MAX_DEPTH) {
    noteCapped(
      expression,
      'chain depth',
      `stopped after ${MAX_DEPTH} steps, the rest was not read`
    );
    return UNRESOLVED;
  }
  const node = unwrap(expression);
  if (!node) return UNRESOLVED;
  const recurse = (next) => keysFrom(next, seen, depth + 1);

  const text = literalText(node);
  if (text !== undefined) return [STATIC(text)];

  // A branch: every arm has to hold up on its own.
  if (ts.isConditionalExpression(node)) {
    return capCandidates([...recurse(node.whenTrue), ...recurse(node.whenFalse)], node, 'ternary');
  }

  // `a ?? b` and `a || b` pick one of two candidates, same as a branch.
  if (
    ts.isBinaryExpression(node) &&
    (node.operatorToken.kind === ts.SyntaxKind.QuestionQuestionToken ||
      node.operatorToken.kind === ts.SyntaxKind.BarBarToken)
  ) {
    return capCandidates([...recurse(node.left), ...recurse(node.right)], node, 'fallback');
  }

  // 'a.b.' + expr builds the key at runtime; only the parent can be checked.
  if (ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.PlusToken) {
    const left = recurse(node.left);
    const right = recurse(node.right);
    const results = [];
    for (const head of left) {
      if (head.kind !== 'static') {
        results.push(head);
        continue;
      }
      for (const tail of right) {
        results.push(tail.kind === 'static' ? STATIC(head.key + tail.key) : PREFIXED(head.key));
      }
    }
    return capCandidates(results, node, 'concatenation');
  }

  if (ts.isTemplateExpression(node)) {
    let heads = [node.head.text];
    for (const span of node.templateSpans) {
      const filled = recurse(span.expression);
      if (!filled.every((item) => item.kind === 'static')) {
        return heads.map((head) => PREFIXED(head));
      }
      const grown = [];
      for (const head of heads) {
        for (const item of filled) grown.push(head + item.key + span.literal.text);
      }
      heads = capCandidates(grown, node, 'template');
    }
    return heads.map((head) => STATIC(head));
  }

  if (ts.isIdentifier(node)) {
    const found = findNamed(node, node.text, new Set(seen));
    if (!found || isFunctionNode(unwrap(found))) return UNRESOLVED;
    return recurse(found);
  }

  // KEYS.BLOCKED - a named entry in a table of keys.
  if (ts.isPropertyAccessExpression(node) && ts.isIdentifier(node.expression)) {
    const found = findNamed(node.expression, node.expression.text, new Set(seen));
    const table = found ? tableEntries(unwrap(found)) : undefined;
    const entry = table?.byName.get(node.name.text);
    return entry ? recurse(entry) : UNRESOLVED;
  }

  // LABEL_KEYS[range] - the index is dynamic, so every entry has to hold up.
  if (ts.isElementAccessExpression(node) && ts.isIdentifier(node.expression)) {
    const found = findNamed(node.expression, node.expression.text, new Set(seen));
    const table = found ? tableEntries(unwrap(found)) : undefined;
    if (!table) return UNRESOLVED;
    const index = literalText(node.argumentExpression);
    if (index !== undefined) {
      const entry = table.byName.get(index);
      return entry ? recurse(entry) : UNRESOLVED;
    }
    return capCandidates(
      table.values.flatMap((entry) => recurse(entry)),
      node,
      'lookup table'
    );
  }

  // labelKeyFor(method) - every key the helper can return.
  if (ts.isCallExpression(node) && ts.isIdentifier(node.expression)) {
    const found = findNamed(node.expression, node.expression.text, new Set(seen));
    const fn = found ? unwrap(found) : undefined;
    if (!isFunctionNode(fn)) return UNRESOLVED;
    const returns = returnExpressions(fn);
    if (!returns) return UNRESOLVED;
    return capCandidates(
      returns.flatMap((entry) => recurse(entry)),
      node,
      'helper call'
    );
  }

  return UNRESOLVED;
}

/** Is this call t(...), i18n.t(...) or i18next.t(...)? */
function isTranslationCall(node) {
  const callee = node.expression;
  if (ts.isIdentifier(callee)) return callee.text === 't';
  if (ts.isPropertyAccessExpression(callee) && callee.name.text === 't') {
    return (
      ts.isIdentifier(callee.expression) && ['i18n', 'i18next'].includes(callee.expression.text)
    );
  }
  return false;
}

/**
 * Is this string literal a translation key written as data? Everything in this
 * app that reaches t() through a variable is declared this way.
 */
function isKeyDeclaration(name, value) {
  if (name && NOT_TRANSLATION_KEYS.has(name)) return false;
  if (!DOTTED_KEY_SHAPE.test(value)) return false;
  return SECTIONS.has(value.split('.')[0]);
}

// ── The check ─────────────────────────────────────────────────────────────────

const errors = [];
const unresolved = [];
const declaredSeen = new Set();
let staticChecked = 0;
let prefixChecked = 0;
let declaredChecked = 0;

function checkStatic(key, where) {
  // Anything that is not a plain key (a sentence, a URL) is not a lookup.
  if (!KEY_SHAPE.test(key)) return;
  staticChecked += 1;
  const missing = LOCALES.filter((name) => !resolvesIn(locales[name], key));
  if (missing.length > 0) errors.push({ ...where, key, missing });
}

function checkPrefix(head, where) {
  const parent = head.replace(/\.[^.]*$/, '');
  if (!parent || !KEY_SHAPE.test(parent)) {
    unresolved.push(where);
    return;
  }
  prefixChecked += 1;
  const missing = LOCALES.filter((name) => !isObjectIn(locales[name], parent));
  if (missing.length > 0) errors.push({ ...where, key: `${parent}.*`, missing });
}

function checkCall(results, where) {
  // A helper with several return paths reaches the same key more than once.
  const seen = new Set();
  for (const result of results) {
    const id = `${result.kind}:${result.key ?? result.head ?? ''}`;
    if (seen.has(id)) continue;
    seen.add(id);
    if (result.kind === 'static') checkStatic(result.key, where);
    else if (result.kind === 'prefixed') checkPrefix(result.head, where);
    else unresolved.push(where);
  }
}

function checkDeclared(key, where) {
  // The same literal is often written in several files; report it once.
  const id = `${where.file}:${where.line}:${key}`;
  if (declaredSeen.has(id)) return;
  declaredSeen.add(id);
  declaredChecked += 1;
  const missing = LOCALES.filter((name) => !resolvesIn(locales[name], key));
  if (missing.length > 0) errors.push({ ...where, key, missing });
}

for (const file of walkDir(SCAN_DIR)) {
  const source = parseFile(file);
  if (!source) continue;
  const relPath = relative(SCAN_DIR, file).split(sep).join('/');
  const lineOf = (node) => source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;

  const visit = (node) => {
    if (ts.isCallExpression(node) && isTranslationCall(node) && node.arguments.length > 0) {
      checkCall(keysFrom(node.arguments[0]), {
        file: relPath,
        line: lineOf(node),
        argument: node.arguments[0].getText(source).replace(/\s+/g, ' ')
      });
    }

    // Keys written as data, checked where they are declared rather than where
    // they are read. A key that only ever reaches t() as a variable has no
    // other place it can be checked.
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
      const parent = node.parent;
      let name;
      if (parent && ts.isPropertyAssignment(parent) && parent.initializer === node) {
        name = propertyName(parent.name);
      } else if (parent && ts.isVariableDeclaration(parent) && parent.initializer === node) {
        name = ts.isIdentifier(parent.name) ? parent.name.text : undefined;
      } else if (
        parent &&
        ts.isArrayLiteralExpression(parent) &&
        parent.parent &&
        ts.isPropertyAssignment(parent.parent)
      ) {
        name = propertyName(parent.parent.name);
      } else {
        name = undefined;
      }
      if (name !== undefined && isKeyDeclaration(name, node.text)) {
        checkDeclared(node.text, { file: relPath, line: lineOf(node) });
      }
    }

    ts.forEachChild(node, visit);
  };
  visit(source);
}

console.log(
  `Summary: ${staticChecked} static keys checked, ${prefixChecked} dynamic prefixes checked, ` +
    `${declaredChecked} declared keys checked, ${unresolved.length} supplied at runtime`
);

if (unresolved.length > 0) {
  const files = [...new Set(unresolved.map((entry) => entry.file))];
  console.log(
    `NOTE: ${unresolved.length} call(s) across ${files.length} file(s) read their key from a value ` +
      `decided at runtime - a stage key the server sent, or a field of a row/tab/option. There is ` +
      `nothing to trace at the call site, but the literals behind them are covered elsewhere: ` +
      `declared keys are checked above where they are written, and signalr.* stage keys are checked ` +
      `on the sending side by validate-stage-keys.mjs. Not failures. ` +
      `Run with --list-unresolved to see them.`
  );
  if (process.argv.includes('--list-unresolved')) {
    for (const entry of unresolved) {
      console.log(`  ${entry.file}:${entry.line}  t(${entry.argument})`);
    }
  }
}

if (cappedSites.length > 0) {
  const counts = new Map();
  for (const site of cappedSites) counts.set(site.cause, (counts.get(site.cause) ?? 0) + 1);
  console.error(
    `\nI18N KEY LINT: analysis stopped short at ${cappedSites.length} site(s), so this run ` +
      `cannot say the keys resolve:\n`
  );
  for (const [cause, count] of counts) console.error(`  ${cause}: ${count} site(s)`);
  console.error('');
  for (const site of cappedSites) {
    console.error(`  ${site.file}:${site.line}`);
    console.error(`    ${site.cause}: ${site.note}\n`);
  }
  console.error(
    'Fix: narrow the expression at those sites, or raise MAX_CANDIDATES / MAX_DEPTH in this\n' +
      'script until every site above is read in full. The keys a cap drops are never checked,\n' +
      'so passing here would report success for an analysis that never finished.\n'
  );
}

if (errors.length > 0) {
  console.error(`\nI18N KEY LINT: ${errors.length} key(s) do not resolve in every locale:\n`);
  for (const err of errors) {
    console.error(`  ${err.file}:${err.line}`);
    console.error(`    ${err.key}`);
    console.error(`    missing from: ${err.missing.join(', ')}\n`);
  }
  console.error(
    'Fix: add the key to every locale in src/i18n/locales. An inline English fallback\n' +
      'hides this at runtime, so the string renders in English for every other language.\n'
  );
}

if (cappedSites.length > 0 || errors.length > 0) {
  process.exit(1);
}

console.log('i18n key lint: PASS (every key resolves in en and zh)');
process.exit(0);
