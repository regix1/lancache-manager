import assert from 'node:assert/strict';
import { readFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
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
 * @param {typescript.CompilerOptions} [compilerOptions] Optional compilation overrides.
 * @returns {string} The compiled JavaScript.
 */
export const transpile = (
  source,
  moduleKind = typescript.ModuleKind.ESNext,
  compilerOptions = {}
) =>
  typescript.transpileModule(source, {
    compilerOptions: {
      module: moduleKind,
      target: typescript.ScriptTarget.ES2022,
      ...compilerOptions
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
 * @param {typescript.CompilerOptions} [compilerOptions] Optional compilation overrides.
 * @returns {Promise<string>} A data URL holding the compiled module.
 */
export const compileToUrl = async (relativePath, aliasUrls = {}, compilerOptions = {}) => {
  const source = await readFile(new URL(relativePath, import.meta.url), 'utf8');
  const resolved = Object.entries(aliasUrls).reduce(
    (text, [alias, url]) =>
      ['from', 'import'].reduce(
        (rewritten, keyword) =>
          rewritten.split(`${keyword} '${alias}'`).join(`${keyword} '${url}'`),
        text.split(`import('${alias}')`).join(`import('${url}')`)
      ),
    transpile(source, typescript.ModuleKind.ESNext, compilerOptions)
  );
  return moduleUrl(resolved);
};

/** Compile a bounded import graph, keeping production modules real and replacing explicit browser boundaries. */
export const compileTree = async (relativePath, aliasUrls = {}) => {
  const cache = new Map();
  const compile = async (url) => {
    if (cache.has(url.href)) return cache.get(url.href);
    const pending = (async () => {
      let source = transpile(await readFile(url, 'utf8')).replaceAll('import.meta.env', '({})');
      const imports = [...source.matchAll(/\b(?:from|import)\s+['"]([^'"]+)['"]/g)].map(
        (match) => match[1]
      );
      for (const name of new Set(imports)) {
        let replacement = aliasUrls[name];
        if (!replacement) {
          let base;
          if (name.startsWith('.')) base = new URL(name, url);
          else if (name.startsWith('@/'))
            base = new URL(`../src/${name.slice(2)}`, import.meta.url);
          else if (/^@(utils|contexts|services|components|hooks|types)\//.test(name))
            base = new URL(`../src/${name.slice(1)}`, import.meta.url);
          if (base) {
            const boundary = Object.entries(aliasUrls).find(
              ([alias]) =>
                alias.startsWith('@/') &&
                new URL(`../src/${alias.slice(2)}`, import.meta.url).href ===
                  base.href
                    .replace(/\/index(?:\.[cm]?[jt]sx?)?$/, '')
                    .replace(/\.[cm]?[jt]sx?$/, '')
            );
            if (boundary) replacement = boundary[1];
            else {
              const candidate = ['', '.ts', '.tsx', '/index.ts', '/index.tsx']
                .map((suffix) => new URL(base.href + suffix))
                .find((file) => existsSync(file) && /\.[cm]?[jt]sx?$/.test(file.pathname));
              assert.ok(candidate, `unresolved production import ${name} from ${url.pathname}`);
              replacement = await compile(candidate);
            }
          } else replacement = import.meta.resolve(name);
        }
        source = source
          .split(`from '${name}'`)
          .join(`from '${replacement}'`)
          .split(`from "${name}"`)
          .join(`from '${replacement}'`)
          .split(`import '${name}'`)
          .join(`import '${replacement}'`);
      }
      return moduleUrl(source);
    })();
    cache.set(url.href, pending);
    return pending;
  };
  return compile(new URL(relativePath, import.meta.url));
};

export const loadNotificationModules = async (
  i18nUrl = moduleUrl('export default {t:(key)=>key};')
) => {
  const aliases = { '@/i18n': i18nUrl };
  const modules = await Promise.all(
    [
      'handlers',
      'constants',
      'notificationStatus',
      'notificationRegistry',
      'recovery',
      'runStore',
      'detailMessageFormatters'
    ].map(
      async (name) => import(await compileTree(`../src/contexts/notifications/${name}.ts`, aliases))
    )
  );
  return Object.assign({}, ...modules);
};

// ── Run rows and the run store, shared by every test that feeds the notification store ──

let runRevision = 0;
const runStartTimes = new Map();

/**
 * The next server revision. Rows take theirs from this one counter, as the server's rows do, so
 * a row built later is always newer; a test that republishes a row or stamps a snapshot asks here.
 *
 * @returns {number} A revision above every row built so far.
 */
export const nextRunRevision = () => ++runRevision;

/**
 * A valid `OperationRun` wire row, as `readOperationRun` accepts it, with `overrides` on top. Each
 * operation id keeps one start time, one second after the previous new id, so the older of two
 * merged runs is decided by time as it is on the server. A terminal row carries its own revision
 * as `completedRevision`, as the server's first terminal row does; pass one to override it.
 *
 * @param {string} operationId The run's operation id.
 * @param {Record<string, unknown>} [overrides] Fields that replace the defaults.
 * @returns {Record<string, unknown>} The row.
 */
export const operationRunRow = (operationId, overrides = {}) => {
  if (!runStartTimes.has(operationId))
    runStartTimes.set(
      operationId,
      new Date(Date.UTC(2026, 8, 22, 10, 0, runStartTimes.size)).toISOString()
    );
  const revision = nextRunRevision();
  const terminal = ['completed', 'failed', 'canceled', 'skipped'].includes(overrides.status);
  return {
    operationId,
    operationType: 'evictionScan',
    name: 'Eviction Scan',
    status: 'running',
    visibility: 'card',
    percentComplete: 0,
    message: 'signalr.evictionScan.scanning',
    startedAt: runStartTimes.get(operationId),
    revision,
    ...(terminal ? { completedRevision: revision } : {}),
    ...overrides
  };
};

/** The fields that make an `operationRunRow` one platform's scheduled prefill run. */
export const prefillRunFields = {
  operationType: 'scheduledPrefill',
  name: 'Scheduled Prefill',
  message: 'signalr.scheduledPrefill.running',
  serviceId: 'Steam',
  scheduleId: 'S1'
};

/**
 * One pushed row applied to a run store the way the provider applies an `OperationUpdated` push,
 * with Keep Notifications Visible off.
 *
 * @param {Record<string, Function>} modules The notification modules (`loadNotificationModules`).
 * @param {object} state The run store state.
 * @param {Record<string, unknown>} row The row.
 * @param {object[]} [localCards] The browser's own cards (a bulk card folds its items).
 * @returns {object} The next state.
 */
export const pushRun = (modules, state, row, localCards = []) =>
  modules.applyRun(state, row, { keepSuccessVisible: false, localCards, pushed: true }).next;

/**
 * A `bulk_removal` card the browser owns, running by default.
 *
 * @param {Record<string, unknown>} details The card's details (`itemTypes`, `currentOperationId`,
 *   `itemOperationIds`, ...).
 * @param {Record<string, unknown>} [overrides] Card fields that replace the defaults (id, status,
 *   message).
 * @returns {object} The card.
 */
export const bulkRemovalCard = (details, overrides = {}) => ({
  id: 'bulk',
  type: 'bulk_removal',
  status: 'running',
  message: 'Removing 1 of 3',
  startedAt: new Date(Date.UTC(2026, 8, 22, 9)),
  details,
  ...overrides
});

const busyRunsUrl = moduleUrl(`
  export const box = { runs: [] };
  export const useNotifications = () => ({ runs: box.runs });
`);

/**
 * Whether `identifier`'s remove button is busy over a run list, through the real
 * `useIsEntityBusy`. React's `useMemo` runs its factory on every call and the notification context
 * hands over `runs`, so no component render is needed.
 *
 * @param {Record<string, unknown>} identifier The entity (`{ kind: 'namedGame', ... }`).
 * @param {object[]} runs The context's `runs`: every live run as a card object.
 * @returns {Promise<boolean>} True while a removal of that entity runs or unwinds.
 */
export const entityBusyFor = async (identifier, runs) => {
  const { box } = await import(busyRunsUrl);
  box.runs = runs;
  const { useIsEntityBusy } = await import(
    await compileToUrl('../src/hooks/useIsEntityBusy.ts', {
      react: moduleUrl('export const useMemo = (fn) => fn();'),
      '../contexts/notifications/useNotifications': busyRunsUrl
    })
  );
  return useIsEntityBusy(identifier);
};

const busyRuns = await import(busyRunsUrl);
const { useOperationBusy } = await import(
  await compileToUrl('../src/hooks/useOperationBusy.ts', {
    react: moduleUrl('export const useMemo = (fn) => fn();'),
    '@contexts/notifications/useNotifications': busyRunsUrl
  })
);

/**
 * Whether a page's `useOperationBusy(options)` flag is set over a run list, through the real hook.
 * React's `useMemo` runs its factory on every call and ignores the dependency array, so this checks
 * what the hook computes, never whether the memo is keyed on the right values.
 *
 * @param {{ types: string[], status?: string | string[] }} options The options the page passes.
 * @param {object[]} runs The context's `runs`: every live run as a card object.
 * @returns {boolean} True when a run of one of `types` has one of the busy statuses.
 */
export const operationBusyFor = (options, runs) => {
  busyRuns.box.runs = runs;
  return useOperationBusy(options);
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

const locales = {
  en: JSON.parse(readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')),
  zh: JSON.parse(readFileSync(new URL('../src/i18n/locales/zh.json', import.meta.url), 'utf8'))
};

/**
 * The i18next lookup a component gets, for one locale: dotted key, then `{{var}}` filled in. Unlike
 * a real i18next instance it fails the test on a key the locale has no string for, instead of
 * printing the key path.
 *
 * @param {'en' | 'zh'} locale Locale file to read.
 * @returns {(key: string, vars?: Record<string, unknown>) => string} The lookup.
 */
export const translator =
  (locale) =>
  (key, vars = {}) => {
    const text = key.split('.').reduce((node, part) => node?.[part], locales[locale]);
    assert.equal(typeof text, 'string', `${locale}.json has no string at ${key}`);
    return text.replace(/{{(\w+)}}/g, (_, name) => String(vars[name]));
  };

/**
 * Parses a TypeScript file into a syntax tree the lifting helpers can walk.
 *
 * @param {string} relativePath Path to the file, relative to the repo's `Web/` directory.
 * @param {typescript.ScriptKind} [scriptKind] TS for a `.ts` file, TSX for a `.tsx` one; picked
 *   from the extension when left out.
 * @returns {typescript.SourceFile} The parsed file, carrying `relativePath` as its file name.
 */
export const parseSource = (
  relativePath,
  scriptKind = relativePath.endsWith('.tsx') ? typescript.ScriptKind.TSX : typescript.ScriptKind.TS
) => {
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
 * The shipped initializer of `const <name> = ...`, evaluated, so a test reads the value the file
 * ships instead of a copy of it. The initializer may use no other name from the file; a global it
 * reads, such as `window`, is taken from `bindings` when given there.
 *
 * @param {string} relativePath Path to the file, relative to the repo's `Web/` directory.
 * @param {string} name Name the value is assigned to.
 * @param {Record<string, unknown>} [bindings] Values bound by name while the initializer runs.
 * @returns {unknown} The evaluated value.
 */
export const declaredValue = (relativePath, name, bindings = {}) => {
  const sourceFile = parseSource(relativePath);
  const declaration = findSoleNode(
    sourceFile,
    `${name} declaration`,
    (node) => typescript.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
  );
  return bindLifted(`() => (${declaration.initializer.getText(sourceFile)})`, bindings)();
};

/**
 * Every order of `steps`, so a test that races several replies runs each one first, last and between.
 *
 * @template T
 * @param {T[]} steps The steps to order.
 * @returns {T[][]} Each permutation of `steps`.
 */
export const ordersOf = (steps) =>
  steps.length <= 1
    ? [steps]
    : steps.flatMap((step, index) =>
        ordersOf([...steps.slice(0, index), ...steps.slice(index + 1)]).map((rest) => [
          step,
          ...rest
        ])
      );

/**
 * The value imports of a file: the local name, the module, and the name the module declares it
 * under. Type-only imports are left out, since nothing is bound for them at run time.
 *
 * @param {string} relativePath Path to the file, relative to the repo's `Web/` directory.
 * @returns {{ local: string, from: string, exported: string }[]} One entry per bound name; a
 *   default import has `exported: 'default'`.
 */
export const importsOf = (relativePath) =>
  parseSource(relativePath, typescript.ScriptKind.TSX)
    .statements.filter(typescript.isImportDeclaration)
    .flatMap((declaration) => {
      const clause = declaration.importClause;
      if (!clause || clause.isTypeOnly) return [];
      const from = declaration.moduleSpecifier.text;
      const found = clause.name ? [{ local: clause.name.text, from, exported: 'default' }] : [];
      const named = clause.namedBindings;
      if (named && typescript.isNamedImports(named))
        found.push(
          ...named.elements
            .filter((element) => !element.isTypeOnly)
            .map((element) => ({
              local: element.name.text,
              from,
              exported: (element.propertyName ?? element.name).text
            }))
        );
      return found;
    });

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
 * @param {typescript.CompilerOptions} [compilerOptions] Optional compilation overrides.
 * @returns {Function} The arrow, ready to call.
 */
export const bindLifted = (arrowSource, bindings, compilerOptions = {}) => {
  const names = Object.keys(bindings);
  const compiled = transpile(
    `const lifted = (${arrowSource});`,
    typescript.ModuleKind.CommonJS,
    compilerOptions
  );
  return new Function(...names, `${compiled}\nreturn lifted;`)(
    ...names.map((name) => bindings[name])
  );
};

/**
 * A React component from a `.tsx` file, compiled with its imports dropped and every name it
 * imports supplied by name through `bindings`, so a test renders the component that ships with
 * stubs at its edges. A binding named like the component itself is ignored.
 *
 * @param {string} path Path to the component, relative to the repo's `Web/` directory.
 * @param {string} name Name the file declares the component under.
 * @param {Record<string, unknown>} bindings Imported name to the value it should read.
 * @returns {Function} The component.
 */
export const make = (path, name, bindings) => {
  const source = parseSource(path, typescript.ScriptKind.TSX);
  const body = source.statements
    .filter((node) => !typescript.isImportDeclaration(node) && !typescript.isExportAssignment(node))
    .map((node) => node.getText(source))
    .join('\n')
    .replace(/\bexport\s+/g, '');
  const code = transpile(body, typescript.ModuleKind.CommonJS, { jsx: typescript.JsxEmit.React });
  const names = Object.keys(bindings).filter((key) => key !== name);
  return new Function(...names, `${code}\nreturn ${name};`)(...names.map((key) => bindings[key]));
};

/**
 * Every stylesheet `npm run build` wrote to `dist/assets/`, the main bundle first, joined into one
 * string a test page can put in a `<style>` tag.
 *
 * @returns {string} The built CSS.
 */
export const builtStyles = () => {
  const assets = new URL('../dist/assets/', import.meta.url);
  return readdirSync(assets)
    .filter((file) => file.endsWith('.css'))
    .sort((a, b) => Number(b.startsWith('index-')) - Number(a.startsWith('index-')))
    .map((file) => readFileSync(new URL(file, assets), 'utf8'))
    .join('\n');
};

/** The origin a browser test loads the built app from; nothing listens there, `server` answers it. */
export const builtAppOrigin = 'http://lancache.test';

/**
 * Runs `body` with a fresh Chromium from the playwright-core install `PLAYWRIGHT_PATH` names,
 * closed afterwards. Asserts first that `npm run build` wrote the app the browser test loads.
 *
 * @param {(browser: import('playwright-core').Browser) => Promise<void>} body The test steps.
 * @returns {Promise<void>}
 */
export const withBrowser = async (body) => {
  assert.ok(
    existsSync(new URL('../dist/index.html', import.meta.url)),
    'run npm run build before this test'
  );
  const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_PATH).href);
  const browser = await chromium.launch({ headless: true });
  try {
    await body(browser);
  } finally {
    await browser.close();
  }
};

const contentTypes = {
  '.html': 'text/html',
  '.js': 'text/javascript',
  '.css': 'text/css',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.woff2': 'font/woff2',
  '.json': 'application/json'
};

/**
 * A Playwright route handler for `page.route('**\/*', ...)` that serves the built app from `dist/`
 * at `builtAppOrigin`. A GET of an `/api/` path in `answers` gets that JSON, every other API call a
 * 503, any other origin and the SignalR hubs are aborted, and an unknown page path gets index.html.
 *
 * @param {Record<string, unknown>} answers JSON answers keyed by API path.
 * @returns {(route: import('playwright-core').Route) => Promise<void>} The route handler.
 */
export const server = (answers) => async (route) => {
  const dist = new URL('../dist/', import.meta.url);
  const url = new URL(route.request().url());
  if (url.origin !== builtAppOrigin || url.pathname.startsWith('/hubs/')) return route.abort();
  if (url.pathname.startsWith('/api/')) {
    const answer = answers[url.pathname];
    return route.request().method() === 'GET' && answer
      ? route.fulfill({ json: answer })
      : route.fulfill({ status: 503, json: {} });
  }
  const file = new URL(`.${decodeURIComponent(url.pathname)}`, dist);
  const found = existsSync(file) && statSync(file).isFile() ? file : new URL('index.html', dist);
  const extension = found.pathname.slice(found.pathname.lastIndexOf('.'));
  return route.fulfill({
    body: readFileSync(found),
    contentType: contentTypes[extension] ?? 'application/octet-stream'
  });
};

/** Ends every SignalR JSON message on the wire. */
const record = '\u001e';

/**
 * The download hub of the live connection, faked over Playwright's WebSocket routing for a page on
 * `builtAppOrigin`. It answers the negotiate request and the handshake, answers the page's hub
 * calls (its group joins) as done, counts the handshakes, sends events, and drops a socket. Set up
 * after `page.route('**\/*', server(answers))`, so its routes are the ones the hub requests take.
 *
 * @param {import('playwright-core').Page} page The page whose hub is faked.
 * @returns {Promise<{
 *   handshakesReach: (count: number) => Promise<void>,
 *   send: (target: string, argument: unknown) => void,
 *   drop: () => void
 * }>} Waits for the page's handshake count, sends an event on the newest socket, and closes it.
 */
export const fakeHub = async (page) => {
  const sockets = [];
  let handshakes = 0;
  const waiters = [];
  await page.route(`${builtAppOrigin}/hubs/downloads/negotiate**`, (route) =>
    route.fulfill({
      json: {
        connectionId: `connection-${sockets.length + 1}`,
        connectionToken: `token-${sockets.length + 1}`,
        negotiateVersion: 1,
        availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }]
      }
    })
  );
  await page.routeWebSocket(/\/hubs\/downloads/, (socket) => {
    sockets.push(socket);
    socket.onMessage((message) => {
      if (typeof message !== 'string') return;
      for (const text of message.split(record).filter(Boolean)) {
        const sent = JSON.parse(text);
        if (sent.protocol) {
          socket.send(`{}${record}`);
          handshakes += 1;
          for (const waiter of waiters.splice(0)) waiter();
        } else if (sent.type === 1 && sent.invocationId) {
          socket.send(`${JSON.stringify({ type: 3, invocationId: sent.invocationId })}${record}`);
        }
      }
    });
  });
  const handshakesReach = async (count) => {
    while (handshakes < count) {
      await new Promise((resolve) => waiters.push(resolve));
    }
  };
  return {
    handshakesReach,
    send: (target, argument) =>
      sockets.at(-1).send(`${JSON.stringify({ type: 1, target, arguments: [argument] })}${record}`),
    drop: () => sockets.at(-1).close({ code: 4000, reason: 'connection lost' })
  };
};

/**
 * Runs in the page through `page.evaluate(settle)`. Awaits every animation that ends, then waits
 * until the page's box sizes hold still for five frames, giving up after 120 frames, so a measure
 * never reads a dialog or card halfway through opening or a layout halfway through a resize.
 *
 * @returns {Promise<void>}
 */
export const settle = async () => {
  const frame = () => new Promise((resolve) => globalThis.requestAnimationFrame(resolve));
  const ending = () =>
    globalThis.document
      .getAnimations()
      .filter((animation) => animation.effect?.getComputedTiming().endTime !== Infinity);
  const shape = () =>
    [...globalThis.document.querySelectorAll('body *')]
      .map(
        (element) =>
          `${element.offsetLeft},${element.offsetTop},${element.offsetWidth},${element.offsetHeight}`
      )
      .join(';');
  let last = '';
  let still = 0;
  for (let frames = 0; still < 5 && frames < 120; frames += 1) {
    await Promise.all(ending().map((animation) => animation.finished.catch(() => null)));
    await frame();
    const now = shape();
    still = now === last ? still + 1 : 0;
    last = now;
  }
};
