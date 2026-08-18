import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * A snapshot fetched before the SignalR subscription was live misses every event raised in the
 * window between the two, and nothing replays them. The hook is the shared answer to that, so it
 * is run here for real - compiled and driven through a sequence of renders - rather than described,
 * and the two properties that pull against each other are asserted together: it must fire on a
 * connection that arrived after the caller's mount fetch, and it must stay silent when the caller
 * mounted into a connection that was already live and whose mount fetch therefore missed nothing.
 */

const hookSource = readFileSync(
  new URL('../src/hooks/useReconnectRefetch.ts', import.meta.url),
  'utf8'
);

/**
 * The smallest React that can run one hook: ordered slots for `useRef`, dependency comparison and
 * a post-render flush for `useEffect`. A data-URL import resolves no bare specifier, so the hook is
 * compiled against this module in place of the real one.
 */
const reactStubSource = `
let slots = null;
let cursor = 0;
let queued = [];

export const useRef = (initial) => {
  if (!slots[cursor]) {
    slots[cursor] = { current: initial };
  }
  return slots[cursor++];
};

export const useEffect = (run, deps) => {
  if (!slots[cursor]) {
    slots[cursor] = { deps: null, ran: false };
  }
  const slot = slots[cursor++];
  const changed = !slot.ran || deps.some((value, index) => !Object.is(value, slot.deps[index]));
  slot.ran = true;
  slot.deps = deps;
  if (changed) {
    queued.push(run);
  }
};

export const createComponent = () => {
  const componentSlots = [];
  return {
    render(body) {
      slots = componentSlots;
      cursor = 0;
      queued = [];
      body();
      const effects = queued;
      queued = [];
      slots = null;
      effects.forEach((run) => run());
    }
  };
};
`;

const reactStubUrl = `data:text/javascript;base64,${Buffer.from(reactStubSource).toString('base64')}`;
const { createComponent } = await import(reactStubUrl);
const { useReconnectRefetch } = await import(
  await compileToUrl('../src/hooks/useReconnectRefetch.ts', { react: reactStubUrl })
);

/**
 * Mounts one caller at the given connection state and hands back a `render` the test drives the
 * connection with. Every render passes a freshly built callback, the way a caller that closes over
 * page or filter state does.
 */
const mountCaller = (connectedAtMount) => {
  const refetches = [];
  const component = createComponent();
  const render = (isConnected) => {
    component.render(() => useReconnectRefetch(isConnected, () => refetches.push(isConnected)));
  };
  render(connectedAtMount);
  return { refetches, render };
};

test('a connection that comes up after the caller mounted refetches', () => {
  const { refetches, render } = mountCaller(false);
  assert.equal(refetches.length, 0, 'nothing to refetch while the socket is still down');

  render(true);

  assert.equal(
    refetches.length,
    1,
    'the mount fetch was taken before the subscription existed, so it has to be replaced'
  );
});

test('a connection already live at mount does not refetch', () => {
  const { refetches, render } = mountCaller(true);
  render(true);

  assert.deepEqual(
    refetches,
    [],
    'the mount fetch ran with the subscription live, so a second request would be a duplicate'
  );
});

test('a caller that mounted into a live connection still refetches after a drop', () => {
  const { refetches, render } = mountCaller(true);

  render(false);
  render(true);
  assert.equal(refetches.length, 1);

  render(false);
  render(true);
  assert.equal(refetches.length, 2, 'every recovery refetches, not just the first');
});

test('a caller that mounted disconnected refetches on the first connect and on each recovery', () => {
  const { refetches, render } = mountCaller(false);

  render(true);
  render(false);
  render(true);

  assert.equal(refetches.length, 2);
});

test('re-rendering with a new callback and an unchanged connection does not refetch', () => {
  const { refetches, render } = mountCaller(false);
  render(true);

  render(true);
  render(true);

  assert.equal(
    refetches.length,
    1,
    'a busy/page/filter change rebuilds the callback and must not fire a request'
  );
});

test('the effect watches the connection alone', () => {
  assert.match(
    hookSource,
    /\}, \[isConnected\]\);/,
    'adding the callback to the dependency list turns every unrelated re-render into a request'
  );
});
