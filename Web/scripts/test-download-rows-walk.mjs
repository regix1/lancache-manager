import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { findSoleNode, parseSource, transpile } from './transpile-module.mjs';

/**
 * The Downloads export writes one line per download, so it reads the rows from
 * `/api/downloads/all`. That route answers one page at a time and takes the id of the last row
 * back as the place to continue from, which means a reader that keeps the first response and
 * stops writes a file holding only the newest rows and looking complete.
 *
 * The method that ships is lifted out of the service and run here rather than restated, so
 * dropping the loop, reusing a stale cursor, or answering a mid-walk failure with the rows
 * gathered so far fails this file instead of shipping a short export.
 *
 * The route answers 404 when the row the cursor names has been deleted since the page before it,
 * so that outcome is a failing request here, not an empty page that ends the walk.
 */

const apiService = parseSource('src/services/api.service.ts');

const methodText = findSoleNode(
  apiService,
  'the getDownloadRows method',
  (node) => ts.isMethodDeclaration(node) && node.name.getText(apiService) === 'getDownloadRows'
).getText(apiService);

assert.ok(methodText.startsWith('static '), 'getDownloadRows is expected to be a static method');

const API_BASE = 'http://cache/api';

const abortError = () => {
  const error = new Error('The operation was aborted.');
  error.name = 'AbortError';
  return error;
};

/**
 * The lifted method, ready to call. `static` comes off because the text is dropped into an object
 * literal, and that object is what `this.getFetchOptions` and `this.handleResponse` resolve
 * against.
 *
 * @param {(callIndex: number) => unknown} respond What the stubbed fetch answers each call with.
 *   Returning a rows array answers that page; returning `{ status }` is a failing response, the way
 *   the route answers a cursor whose row is gone; throwing fails that request outright.
 * @returns {{ getDownloadRows: Function, urls: string[], signals: (AbortSignal|undefined)[] }} The
 *   method, the URLs it asked for, and the signal each request carried.
 */
const liftGetDownloadRows = (respond) => {
  const urls = [];
  const signals = [];
  const compiled = transpile(
    `const holder = { ${methodText.slice('static '.length)} };`,
    ts.ModuleKind.CommonJS
  );
  const holder = new Function('API_BASE', 'fetch', 'isAbortError', `${compiled}\nreturn holder;`)(
    API_BASE,
    async (url, options) => {
      urls.push(url);
      signals.push(options?.signal);
      if (options?.signal?.aborted) throw abortError();
      return respond(urls.length - 1);
    },
    (error) => error.name === 'AbortError'
  );
  // The two helpers the method calls on the class. getFetchOptions hands back what it was given so
  // the signal is visible here; handleResponse throws for a failing response the way the shipped
  // one does, and otherwise hands back the array the responder answered with.
  holder.getFetchOptions = (options) => options;
  holder.handleResponse = async (response) => {
    if (!Array.isArray(response)) throw new Error(`HTTP ${response.status}`);
    return response;
  };
  return { getDownloadRows: (...args) => holder.getDownloadRows(...args), urls, signals };
};

const row = (id) => ({ id, service: 'steam', clientIp: '10.0.0.7', totalBytes: 1024 });

/** The afterId each request carried, `null` for the first one that carries none. */
const cursors = (urls) => urls.map((url) => new URL(url).searchParams.get('afterId'));

test('the walk continues from the last row of the page before it', async () => {
  const pages = [[row(9), row(8)], [row(7), row(6)], []];
  const { getDownloadRows, urls } = liftGetDownloadRows((callIndex) => pages[callIndex]);

  const rows = await getDownloadRows();

  assert.deepEqual(
    rows.map((entry) => entry.id),
    [9, 8, 7, 6],
    'every page is kept, in the order the server sent them'
  );
  assert.equal(urls.length, 3, 'two pages of rows plus the empty page that ends the walk');
  assert.deepEqual(cursors(urls), [null, '8', '6'], 'each request continues from the last row');
});

test('an empty first page is the whole answer', async () => {
  const { getDownloadRows, urls } = liftGetDownloadRows(() => []);

  const rows = await getDownloadRows();

  assert.deepEqual(rows, [], 'nothing to export');
  assert.equal(urls.length, 1, 'an empty page ends the walk immediately');
});

test('the range and the tagged event go on every request of the walk', async () => {
  const pages = [[row(4)], []];
  const { getDownloadRows, urls } = liftGetDownloadRows((callIndex) => pages[callIndex]);

  await getDownloadRows(1735689600, 1738368000, 12);

  for (const url of urls) {
    const params = new URL(url).searchParams;
    assert.equal(params.get('startTime'), '1735689600', `startTime missing from ${url}`);
    assert.equal(params.get('endTime'), '1738368000', `endTime missing from ${url}`);
    assert.equal(params.get('eventId'), '12', `eventId missing from ${url}`);
  }
});

test('a failure part way through the walk rejects instead of answering with a short export', async () => {
  const { getDownloadRows } = liftGetDownloadRows((callIndex) => {
    if (callIndex === 0) return [row(9), row(8)];
    throw new Error('connection reset');
  });

  await assert.rejects(getDownloadRows(), /connection reset/);
});

// The row the cursor names can be deleted between two requests - a retention pass, a wipe. The
// route answers 404 rather than an empty page, because an empty page reads as "the set ended here"
// and would write a file holding only the rows gathered before it.
test('a cursor whose row is gone rejects instead of ending the walk early', async () => {
  const { getDownloadRows, urls } = liftGetDownloadRows((callIndex) =>
    callIndex === 0 ? [row(9), row(8)] : { status: 404 }
  );

  await assert.rejects(getDownloadRows(), /HTTP 404/);
  assert.equal(urls.length, 2, 'the walk stops at the request that failed');
});

// A hundred thousand rows is two hundred requests. Leaving the page has to stop them, so the
// caller's signal reaches every request of the walk and not just the first.
test('the caller can stop the walk part way with its signal', async () => {
  const controller = new AbortController();
  const { getDownloadRows, urls, signals } = liftGetDownloadRows((callIndex) => {
    if (callIndex === 0) return [row(9), row(8)];
    if (callIndex === 1) {
      controller.abort();
      return [row(7), row(6)];
    }
    // Never reached while the signal travels: the request above it is refused before it is sent.
    return [];
  });

  await assert.rejects(getDownloadRows(undefined, undefined, undefined, controller.signal), {
    name: 'AbortError'
  });
  assert.equal(urls.length, 3, 'the request after the abort is the one that stops');
  assert.ok(
    signals.every((signal) => signal === controller.signal),
    'every request of the walk carries the signal, not only the first'
  );
});
