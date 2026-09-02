import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import typescript from 'typescript';

/**
 * The test scripts exercise product source directly, and node cannot load TypeScript, so every one of
 * them compiles what it is about to run. This is that step, in one place.
 *
 * Paths are resolved against this directory, which is also where the test scripts live, so a caller
 * passes the same `../src/...` path it would have passed to its own `new URL(path, import.meta.url)`.
 *
 * Some of the code under test cannot be imported at all: it lives inside a React component or a
 * hook and is never exported. The lifting helpers at the bottom read that code straight out of the
 * source file and run it with its free variables supplied by name, so a test drives the arrow that
 * ships rather than a copy of it written in the test.
 */

/**
 * Compiles TypeScript source text to JavaScript.
 *
 * @param {string} source TypeScript source text.
 * @param {typescript.ModuleKind} [moduleKind] Module format the caller can consume. ESNext for an
 *   `import()`, CommonJS for source that will be run through `new Function`.
 * @returns {string} The compiled JavaScript.
 */
export const transpile = (source, moduleKind = typescript.ModuleKind.ESNext) =>
  typescript.transpileModule(source, {
    compilerOptions: {
      module: moduleKind,
      target: typescript.ScriptTarget.ES2022
    }
  }).outputText;

/**
 * A data URL `import()` can load, holding the JavaScript a caller hands over. This is how a test
 * substitutes a stub for a module the code under test imports.
 *
 * @param {string} source JavaScript source text.
 * @returns {string} A data URL holding it.
 */
export const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

/**
 * Compiles a TypeScript file to a data URL that `import()` can load.
 *
 * A data-URL import has no path aliases, so any aliased dependency has to be compiled first and its
 * own data URL substituted for the alias here. Only the import specifiers are rewritten: a package
 * whose name the file also compares against as a plain string (`format === 'toml'`) would otherwise
 * have that comparison rewritten too, and the branch behind it would stop being reachable.
 *
 * @param {string} relativePath Path to the TypeScript file, relative to the scripts directory.
 * @param {Record<string, string>} [aliasUrls] Import alias to the data URL that replaces it.
 * @returns {Promise<string>} A data URL holding the compiled module.
 */
export const compileToUrl = async (relativePath, aliasUrls = {}) => {
  const source = await readFile(new URL(relativePath, import.meta.url), 'utf8');
  const resolved = Object.entries(aliasUrls).reduce(
    (text, [alias, url]) =>
      ['from', 'import'].reduce(
        (rewritten, keyword) =>
          rewritten.split(`${keyword} '${alias}'`).join(`${keyword} '${url}'`),
        text.split(`import('${alias}')`).join(`import('${url}')`)
      ),
    transpile(source)
  );
  return moduleUrl(resolved);
};

/**
 * The `localStorage` the product code reads, with nothing on disk behind it. Assigned to
 * `globalThis.localStorage` by a test whose code under test persists or clears a notification.
 */
export class MemoryStorage {
  #values = new Map();

  getItem(key) {
    return this.#values.get(key) ?? null;
  }

  setItem(key, value) {
    this.#values.set(key, String(value));
  }

  removeItem(key) {
    this.#values.delete(key);
  }
}

/**
 * Parses a TypeScript file into a syntax tree the lifting helpers can walk.
 *
 * @param {string} relativePath Path to the file, relative to the repo's `Web/` directory.
 * @param {typescript.ScriptKind} [scriptKind] TS for a `.ts` file, TSX for a `.tsx` one.
 * @returns {typescript.SourceFile} The parsed file, carrying `relativePath` as its file name.
 */
export const parseSource = (relativePath, scriptKind = typescript.ScriptKind.TS) => {
  const source = readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');
  return typescript.createSourceFile(
    relativePath,
    source,
    typescript.ScriptTarget.Latest,
    true,
    scriptKind
  );
};

/**
 * Every node in the tree that `matches` accepts, in source order.
 *
 * @param {typescript.SourceFile} sourceFile Parsed file to walk.
 * @param {(node: typescript.Node) => boolean} matches Node test.
 * @returns {typescript.Node[]} The matching nodes.
 */
export const collectNodes = (sourceFile, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) found.push(node);
    typescript.forEachChild(node, visit);
  };
  typescript.forEachChild(sourceFile, visit);
  return found;
};

/**
 * The single node `matches` accepts, failing the test when there is not exactly one. Lifting code
 * by shape only holds while the shape is unique, so an ambiguous match has to stop the test rather
 * than pick a node and carry on against the wrong one.
 *
 * @param {typescript.SourceFile} sourceFile Parsed file to walk.
 * @param {string} label What the caller was looking for, for the failure message.
 * @param {(node: typescript.Node) => boolean} matches Node test.
 * @returns {typescript.Node} The sole matching node.
 */
export const findSoleNode = (sourceFile, label, matches) => {
  const found = collectNodes(sourceFile, matches);
  assert.equal(found.length, 1, `expected exactly one ${label} in ${sourceFile.fileName}`);
  return found[0];
};

/**
 * Source text of the arrow function assigned to a `const <constName> = (...) => {...}`.
 *
 * @param {string} relativePath Path to the file, relative to the repo's `Web/` directory.
 * @param {string} constName Name the arrow is assigned to.
 * @returns {string} The arrow's source text.
 */
export const liftConstArrow = (relativePath, constName) => {
  const sourceFile = parseSource(relativePath);
  const declaration = findSoleNode(
    sourceFile,
    `${constName} declaration`,
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === constName &&
      node.initializer !== undefined &&
      typescript.isArrowFunction(node.initializer)
  );
  return declaration.initializer.getText(sourceFile);
};

/**
 * Source text of the arrow handed to a React hook inside a component.
 *
 * An initializer, an effect body or a handler wrapped in `useCallback` is never exported, so the
 * one that ships can only be driven by lifting it out of the file. `contains` picks which of a
 * file's several `useState`/`useEffect`/`useCallback` calls is meant - a storage key, a call the
 * body makes - and the test fails rather than guesses when it matches more than one.
 *
 * @param {string} relativePath Path to the component, relative to the repo's `Web/` directory.
 * @param {string} hookName Hook the arrow is passed to, e.g. 'useState'.
 * @param {string} contains Text that appears in the wanted arrow and in no sibling.
 * @returns {string} The arrow's source text.
 */
export const liftHookCallback = (relativePath, hookName, contains) => {
  const sourceFile = parseSource(relativePath, typescript.ScriptKind.TSX);
  const call = findSoleNode(sourceFile, `${hookName} call for ${contains}`, (node) => {
    if (!typescript.isCallExpression(node) || node.arguments.length === 0) return false;
    const callee = typescript.isPropertyAccessExpression(node.expression)
      ? node.expression.name.getText(sourceFile)
      : node.expression.getText(sourceFile);
    if (callee !== hookName) return false;
    const [argument] = node.arguments;
    return typescript.isArrowFunction(argument) && argument.getText(sourceFile).includes(contains);
  });
  return call.arguments[0].getText(sourceFile);
};

/**
 * Compiles a lifted arrow and binds its free variables BY NAME. Every name the arrow reads from
 * its enclosing scope has to appear in `bindings`, or calling it throws a ReferenceError - nothing
 * type-checks these scripts, so that is the only warning a moved or renamed free variable gives.
 *
 * @param {string} arrowSource Source text of the arrow, from one of the lift helpers.
 * @param {Record<string, unknown>} bindings Free variable name to the value it should read.
 * @returns {Function} The arrow, ready to call.
 */
export const bindLifted = (arrowSource, bindings) => {
  const names = Object.keys(bindings);
  const compiled = transpile(`const lifted = (${arrowSource});`, typescript.ModuleKind.CommonJS);
  return new Function(...names, `${compiled}\nreturn lifted;`)(
    ...names.map((name) => bindings[name])
  );
};
