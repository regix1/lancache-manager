import { existsSync } from 'node:fs';
import { posix } from 'node:path';
import React from 'react';
import typescript from 'typescript';
import { compileTree, importsOf, make, parseSource } from './transpile-module.mjs';

/**
 * A shipped component drawn together with the feature components it draws, so a test reads what the
 * screen shows however those controls are split across files. Every component under
 * `src/components/features/` that a drawn file imports is compiled and drawn in place, on the same
 * hook store. A name in `shared` is bound as given; a plain `.ts` module of the app (a key table, a
 * helper) is the real module; any other import is a stub that draws nothing, so a test still reads
 * the props a `ui/` control was handed.
 */

/** Draws nothing; the test reads the props it was handed from the element tree. */
export const inert = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** The app file an import names, relative to `Web/`, or null for a package. */
const appFile = (from, importer) => {
  let base = null;
  if (from.startsWith('.')) base = posix.join(posix.dirname(importer), from);
  else if (from.startsWith('@/')) base = `src/${from.slice('@/'.length)}`;
  else if (/^@(utils|contexts|services|components|hooks|types)\//.test(from))
    base = `src/${from.slice(1)}`;
  if (base === null) return null;
  return (
    ['', '.tsx', '.ts', '/index.ts', '/index.tsx']
      .map((suffix) => `${base}${suffix}`)
      .find((file) => /\.tsx?$/.test(file) && existsSync(new URL(`../${file}`, import.meta.url))) ??
    null
  );
};

/** The name a file declares its default export under (`export default Name`). */
const defaultExportName = (path) => {
  const sourceFile = parseSource(path, typescript.ScriptKind.TSX);
  const assignment = sourceFile.statements.find(typescript.isExportAssignment);
  return assignment.expression.getText(sourceFile);
};

/** The hook functions in `shared`, so a file that calls `React.useState` and one that imports it agree. */
const hooksOf = (shared) =>
  Object.fromEntries(
    Object.entries(shared).filter(([key]) => /^use[A-Z]/.test(key) && key in React)
  );

/**
 * The component `name` in `path`, with every feature component it draws compiled the same way.
 *
 * @param {string} path The root component file, relative to `Web/`.
 * @param {string} name The name the file declares the root component under.
 * @param {Record<string, unknown>} shared Values bound by import name in every drawn file: the hook
 *   store's hooks, `React`, contexts, services and the `ui/` stubs a test reads.
 * @returns {Promise<{ Root: Function, draw: (node: unknown) => unknown }>} The root component, and
 *   the step that draws the feature components in a tree the root returned.
 */
export const featureTree = async (path, name, shared) => {
  const compiled = new Map();
  const components = new Set();
  const compile = async (file, declared) => {
    const key = `${file}#${declared}`;
    if (compiled.has(key)) return compiled.get(key);
    const bindings = {};
    for (const { local, from, exported } of importsOf(file)) {
      const source = local in shared ? null : appFile(from, file);
      if (local in shared) bindings[local] = shared[local];
      else if (source?.startsWith('src/components/features/') && source.endsWith('.tsx'))
        bindings[local] = await compile(
          source,
          exported === 'default' ? defaultExportName(source) : exported
        );
      else if (source?.endsWith('.ts'))
        bindings[local] = (await import(await compileTree(`../${source}`)))[exported];
      else bindings[local] = inert(local);
    }
    const component = make(file, declared, {
      ...bindings,
      React: shared.React,
      ...hooksOf(shared)
    });
    compiled.set(key, component);
    components.add(component);
    return component;
  };
  const Root = await compile(path, name);
  const draw = (node) => {
    if (Array.isArray(node)) return node.map(draw);
    if (!React.isValidElement(node)) return node;
    if (components.has(node.type)) return draw(node.type(node.props));
    const props = Object.fromEntries(
      Object.entries(node.props).map(([prop, value]) => [prop, draw(value)])
    );
    return React.cloneElement(node, props);
  };
  return { Root, draw };
};
