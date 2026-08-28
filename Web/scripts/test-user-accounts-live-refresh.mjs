import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl, transpile } from './transpile-module.mjs';

/**
 * The accounts table is loaded once and never told anything again, so a second administrator's
 * rename, role change, disable or delete sits invisible until somebody reloads the page. The event
 * that fixes that has to exist on both sides of the wire, be sent by the routes that change a row,
 * and be listened for by the screen, and none of the three is checked by the compiler: the names
 * are string literals in C#, in a TypeScript array and in a subscription call.
 */

const EVENT = 'AccountsChanged';
const SCREEN = 'src/components/features/user/UserAccounts.tsx';

const readRepoFile = (relativePath) =>
  readFileSync(new URL(`../../${relativePath}`, import.meta.url), 'utf8');

const parse = (relativePath) => {
  const source = readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');
  return ts.createSourceFile(relativePath, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
};

const collect = (sourceFile, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) {
      found.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);
  return found;
};

/** Every `on(...)` / `off(...)` call in a file, as [firstArgumentText, secondArgumentText]. */
const hubCalls = (sourceFile, name) =>
  collect(
    sourceFile,
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(sourceFile) === name &&
      node.arguments.length === 2
  ).map((node) => node.arguments.map((argument) => argument.getText(sourceFile)));

const { SIGNALR_EVENTS } = await import(
  await compileToUrl('../src/contexts/SignalRContext/types.ts')
);

test('the event name exists on both sides of the wire', () => {
  assert.ok(
    SIGNALR_EVENTS.includes(EVENT),
    `${EVENT} is missing from SIGNALR_EVENTS, so the context never dispatches it`
  );
  assert.match(
    readRepoFile('Api/LancacheManager/Hubs/SignalREvents.cs'),
    new RegExp(`public const string ${EVENT} = "${EVENT}";`),
    `${EVENT} is missing from SignalREvents.cs, so nothing on the server can send it`
  );
});

test('every route that changes an account row sends it', () => {
  const controller = readRepoFile('Api/LancacheManager/Controllers/Auth/AccountsController.cs');
  const routes = [
    'CreateAccountAsync',
    'EditAccountAsync',
    'SetRoleAsync',
    'SetDisabledAsync',
    'DeleteAccountAsync',
    'WipeAccountsAsync'
  ];

  // Each method body runs to the start of the next one, which is enough to tell whose emit is whose
  // because the six are declared in this order and nothing else in the file emits.
  const bodies = routes.map((route, index) => {
    const start = controller.indexOf(`> ${route}(`);
    assert.notEqual(start, -1, `${route} was not found; the route list here is out of date`);
    const next = index + 1 < routes.length ? controller.indexOf(`> ${routes[index + 1]}(`) : -1;
    return controller.slice(start, next === -1 ? undefined : next);
  });

  routes.forEach((route, index) => {
    assert.ok(
      bodies[index].includes(`SignalREvents.${EVENT}`),
      `${route} changes an account row without telling anyone`
    );
  });
});

test('the screen subscribes and unsubscribes with one handler', () => {
  const sourceFile = parse(SCREEN);
  const subscribed = hubCalls(sourceFile, 'on').filter(([event]) => event.includes(EVENT));
  const unsubscribed = hubCalls(sourceFile, 'off').filter(([event]) => event.includes(EVENT));

  assert.equal(subscribed.length, 1, `expected one ${EVENT} subscription in ${SCREEN}`);
  assert.equal(unsubscribed.length, 1, `expected one ${EVENT} unsubscription in ${SCREEN}`);
  assert.equal(
    subscribed[0][1],
    unsubscribed[0][1],
    'off must be given the same handler reference as on, or the subscription outlives the screen'
  );
});

test('a reconnect reloads the list without emptying the table', () => {
  const sourceFile = parse(SCREEN);
  const calls = collect(
    sourceFile,
    (node) =>
      ts.isCallExpression(node) && node.expression.getText(sourceFile) === 'useReconnectRefetch'
  );

  assert.equal(calls.length, 1, `expected exactly one reconnect refetch in ${SCREEN}`);
  assert.equal(calls[0].arguments[0].getText(sourceFile), 'isConnected');

  const callback = calls[0].arguments[1].getText(sourceFile);
  const loads = [];
  const compiled = transpile(`const lifted = (${callback});`, ts.ModuleKind.CommonJS);
  new Function('loadAccounts', `${compiled}\nreturn lifted;`)((showLoading) =>
    loads.push(showLoading)
  )();

  assert.deepEqual(
    loads,
    [false],
    'a recovery reload must not swap the rows for the loading state the first load shows'
  );
});
