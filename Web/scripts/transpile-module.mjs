import { readFile } from 'node:fs/promises';
import typescript from 'typescript';

/**
 * The test scripts exercise product source directly, and node cannot load TypeScript, so every one of
 * them compiles what it is about to run. This is that step, in one place.
 *
 * Paths are resolved against this directory, which is also where the test scripts live, so a caller
 * passes the same `../src/...` path it would have passed to its own `new URL(path, import.meta.url)`.
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
 * Compiles a TypeScript file to a data URL that `import()` can load.
 *
 * A data-URL import has no path aliases, so any aliased dependency has to be compiled first and its
 * own data URL substituted for the alias here.
 *
 * @param {string} relativePath Path to the TypeScript file, relative to the scripts directory.
 * @param {Record<string, string>} [aliasUrls] Import alias to the data URL that replaces it.
 * @returns {Promise<string>} A data URL holding the compiled module.
 */
export const compileToUrl = async (relativePath, aliasUrls = {}) => {
  const source = await readFile(new URL(relativePath, import.meta.url), 'utf8');
  const resolved = Object.entries(aliasUrls).reduce(
    (text, [alias, url]) => text.split(`'${alias}'`).join(`'${url}'`),
    transpile(source)
  );
  return `data:text/javascript;base64,${Buffer.from(resolved).toString('base64')}`;
};
