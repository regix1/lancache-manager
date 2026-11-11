import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import {
  bindLifted,
  findSoleNode,
  liftHookCallback,
  parseSource,
  translator
} from './transpile-module.mjs';

/**
 * The domain source footer shows the last refresh failure. A new refresh keeps that line on screen
 * while its request runs, and only the request's answer changes it: a failure replaces it, a
 * success removes it. The Refresh handler is lifted from the shipped file and driven step by step.
 */

const SECTION = 'src/components/features/management/status-check/StatusCheckSection.tsx';

test('the refresh failure line changes only when a refresh answers', async () => {
  const page = { refreshError: 'earlier failure', refreshing: false, reloadedWith: null };
  const answers = [];
  const handleRefreshDomains = bindLifted(
    liftHookCallback(SECTION, 'useCallback', 'refreshCacheDomains'),
    {
      setRefreshingDomains: (value) => {
        page.refreshing = value;
      },
      setRefreshError: (value) => {
        page.refreshError = value;
      },
      ApiService: {
        refreshCacheDomains: () =>
          new Promise((resolve, reject) => {
            answers.push({ resolve, reject });
          })
      },
      reloadDomains: async (source) => {
        page.reloadedWith = source;
      },
      getErrorMessage: (error) => error.message
    }
  );

  const failing = handleRefreshDomains();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(page.refreshing, true);
  assert.equal(page.refreshError, 'earlier failure', 'the click leaves the failure line in place');
  answers[0].reject(new Error('resolver unreachable'));
  await failing;
  assert.equal(page.refreshError, 'resolver unreachable', 'a new failure replaces the old one');
  assert.equal(page.refreshing, false);

  const succeeding = handleRefreshDomains();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(page.refreshError, 'resolver unreachable', 'the retry click leaves it in place');
  answers[1].resolve({ domainsSource: 'uklans' });
  await succeeding;
  assert.equal(page.refreshError, null, 'a successful refresh removes the failure line');
  assert.equal(page.reloadedWith, 'uklans');
  assert.equal(page.refreshing, false);
});

test('a list reload that fails after a refresh that worked shows its own line', async () => {
  const page = {
    refreshError: null,
    reloadError: null,
    refreshing: false,
    domainGroups: ['steam'],
    status: { domainsSource: 'old' }
  };
  let listAnswer = () => Promise.reject(new Error('list read failed'));
  const reloadDomains = bindLifted(liftHookCallback(SECTION, 'useCallback', 'getCacheDomains()'), {
    statusVersionRef: { current: 0 },
    setStatus: (update) => {
      page.status = update(page.status);
    },
    setDomainGroups: (value) => {
      page.domainGroups = value;
    },
    setReloadError: (value) => {
      page.reloadError = value;
    },
    ApiService: { getCacheDomains: () => listAnswer() },
    getErrorMessage: (error) => error.message
  });
  const handleRefreshDomains = bindLifted(
    liftHookCallback(SECTION, 'useCallback', 'refreshCacheDomains'),
    {
      setRefreshingDomains: (value) => {
        page.refreshing = value;
      },
      setRefreshError: (value) => {
        page.refreshError = value;
      },
      ApiService: { refreshCacheDomains: async () => ({ domainsSource: 'uklans' }) },
      reloadDomains,
      getErrorMessage: (error) => error.message
    }
  );

  await handleRefreshDomains();
  assert.equal(page.refreshError, null, 'the refresh worked, so no refresh failure is shown');
  assert.equal(page.reloadError, 'list read failed', 'the reload failure is shown as its own');
  assert.deepEqual(page.domainGroups, ['steam'], 'the earlier list stays on screen');
  assert.deepEqual(page.status, { domainsSource: 'uklans' });
  assert.equal(page.refreshing, false);

  // The push sent after a refresh reloads the list again; a read that works clears the line.
  listAnswer = async () => ({ services: ['steam', 'epic'] });
  await reloadDomains('uklans');
  assert.equal(page.reloadError, null);
  assert.deepEqual(page.domainGroups, ['steam', 'epic']);

  // A reconnect read that loads the list makes the shown list current, so the line goes too.
  page.reloadError = 'list read failed';
  const loadAll = bindLifted(liftHookCallback(SECTION, 'useCallback', 'getCacheDomains(signal)'), {
    mockMode: false,
    ApiService: {
      getStatusCheck: async () => Promise.reject(new Error('status read failed')),
      getCacheDomains: async () => ({ services: ['steam'] })
    },
    finishedOperationIdsRef: { current: new Set() },
    currentOperationRef: { current: null },
    statusVersionRef: { current: 0 },
    setStatus: () => undefined,
    setIsRunning: () => undefined,
    setStatusError: () => undefined,
    setDomainGroups: (value) => {
      page.domainGroups = value;
    },
    setReloadError: (value) => {
      page.reloadError = value;
    },
    setIsLoading: () => undefined,
    isAbortError: () => false,
    getErrorMessage: (error) => error.message
  });
  await loadAll();
  assert.equal(page.reloadError, null);
  assert.deepEqual(page.domainGroups, ['steam']);
});

test('the footer names a failed list reload apart from a failed refresh', () => {
  const footer = parseSource(
    'src/components/features/management/status-check/DomainSourceFooter.tsx',
    typescript.ScriptKind.TSX
  );
  const arrow = findSoleNode(
    footer,
    'DomainSourceFooter declaration',
    (node) =>
      typescript.isVariableDeclaration(node) && node.name.getText(footer) === 'DomainSourceFooter'
  ).initializer.getText(footer);
  const DomainSourceFooter = bindLifted(
    arrow,
    {
      React,
      useTranslation: () => ({ t: translator('en') }),
      Button: ({ children }) => React.createElement('button', null, children),
      formatRelativeTime: () => 'now',
      formatRepoShortName: () => 'repo'
    },
    { jsx: typescript.JsxEmit.React }
  );
  const render = (props) =>
    renderToStaticMarkup(
      React.createElement(DomainSourceFooter, {
        source: null,
        onRefresh: () => undefined,
        refreshing: false,
        refreshError: null,
        reloadError: null,
        ...props
      })
    );

  const reloadFailed = render({ reloadError: 'list read failed' });
  assert.match(reloadFailed, /The domain list could not be reloaded: list read failed/);
  assert.doesNotMatch(reloadFailed, /Refresh failed/);
  assert.doesNotMatch(render({}), /could not be reloaded/);
});
