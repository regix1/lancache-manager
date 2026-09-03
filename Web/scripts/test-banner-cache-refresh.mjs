import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

/**
 * Runtime properties of the banner refresh path, all driven against the shipping source.
 *
 * Every banner URL carries the version its artwork was stored at, so replacing one game's art
 * changes one URL and leaves every other banner in the browser's cache. That version comes from the
 * same response the row already reads to decide whether the banner renders at all, so it is known at
 * first paint and the element is never given one URL and then handed another. Below that,
 * `useAvailableGameImages` keeps one set at module scope shared by every mounted row, so it needs a
 * bound on how long that set is treated as current and a way to drop it.
 */

/**
 * `transpile` in the shared harness parses every file as `.ts`, which turns a `.tsx` return into
 * garbage assignments, so a component that returns JSX is compiled here with JSX turned on.
 */
const compileComponent = (relativePath) => {
  const source = readFileSync(new URL(relativePath, import.meta.url), 'utf8');
  return ts.transpileModule(source, {
    fileName: relativePath,
    compilerOptions: {
      module: ts.ModuleKind.ESNext,
      target: ts.ScriptTarget.ES2022,
      jsx: ts.JsxEmit.React
    }
  }).outputText;
};

/**
 * The smallest React that can run one component: ordered slots for state, memos and effects, a
 * context read that returns whatever the test's provider currently holds, and a render that keeps
 * going while a state write from an effect dirties it, which is what React does when an effect
 * clears a failure flag.
 */
const reactStubSource = `
let slots = null;
let cursor = 0;
let queued = [];
let dirty = false;

export const useState = (initial) => {
  if (!slots[cursor]) {
    slots[cursor] = { value: initial };
  }
  const slot = slots[cursor++];
  const setValue = (next) => {
    const value = typeof next === 'function' ? next(slot.value) : next;
    if (Object.is(value, slot.value)) return;
    slot.value = value;
    dirty = true;
  };
  return [slot.value, setValue];
};

export const useMemo = (build, deps) => {
  if (!slots[cursor]) {
    slots[cursor] = { value: build(), deps };
    return slots[cursor++].value;
  }
  const slot = slots[cursor++];
  if (deps.some((value, index) => !Object.is(value, slot.deps[index]))) {
    slot.value = build();
    slot.deps = deps;
  }
  return slot.value;
};

export const useCallback = (fn, deps) => useMemo(() => fn, deps);

export const useEffect = (run, deps) => {
  if (!slots[cursor]) {
    slots[cursor] = { deps: null, ran: false, cleanup: null };
  }
  const slot = slots[cursor++];
  const changed = !slot.ran || deps.some((value, index) => !Object.is(value, slot.deps[index]));
  slot.ran = true;
  slot.deps = deps;
  if (changed) {
    queued.push({ slot, run });
  }
};

export const useContext = (context) => context.currentValue;

export const createElement = (type, props) => ({ type, props });

export default { createElement };

/** Mounts one component body and hands back a view whose \`output\` is its last render. */
export const mount = (body) => {
  const componentSlots = [];
  const view = { output: null };
  view.render = () => {
    let passes = 0;
    do {
      dirty = false;
      slots = componentSlots;
      cursor = 0;
      queued = [];
      view.output = body();
      const effects = queued;
      queued = [];
      slots = null;
      for (const { slot, run } of effects) {
        if (slot.cleanup) slot.cleanup();
        slot.cleanup = run() || null;
      }
      passes += 1;
      assert(passes <= 10, 'render did not settle');
    } while (dirty);
  };
  return view;
};

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
`;

const reactStubUrl = moduleUrl(reactStubSource);
const { mount } = await import(reactStubUrl);

/** Stands in for the hoisted provider: the test writes the version every consumer reads. */
const imageCacheStubSource = `export const ImageCacheContext = { currentValue: 0 };`;
const imageCacheStubUrl = moduleUrl(imageCacheStubSource);
const { ImageCacheContext } = await import(imageCacheStubUrl);

const apiStubSource = `
let calls = 0;
let held = null;
let images = { '730': 1 };

export const callCount = () => calls;

/** What the next /available answer reports: each id the server can serve, with its stored version. */
export const setAvailable = (next) => {
  images = next;
};

/** Stops answering immediately, so a test can settle two overlapping requests out of order. */
export const holdResponses = () => {
  held = [];
};

export const answer = (index, next) => held[index](next);

export default {
  getAvailableGameImages: () => {
    calls += 1;
    if (!held) return Promise.resolve(images);
    return new Promise((resolve) => held.push(resolve));
  }
};
`;
const apiStubUrl = moduleUrl(apiStubSource);
const { answer, callCount, holdResponses, setAvailable } = await import(apiStubUrl);

const availableImagesUrl = await compileToUrl('../src/hooks/useAvailableGameImages.ts', {
  react: reactStubUrl,
  '@components/common/ImageCacheContext': imageCacheStubUrl,
  '@services/api.service': apiStubUrl
});
const { useAvailableGameImages, resetAvailableGameImages } = await import(availableImagesUrl);

const gameImageUrl = moduleUrl(
  compileComponent('../src/components/common/GameImage.tsx')
    .split("'react'")
    .join(`'${reactStubUrl}'`)
    .split("'@hooks/useAvailableGameImages'")
    .join(`'${availableImagesUrl}'`)
    // Vite supplies `import.meta.env`; node does not, and the file only reads an override off it.
    .split('import.meta.env')
    .join('({})')
);
const { GameImage } = await import(gameImageUrl);

/** A banner whose miss the test is not measuring. */
const ignoreMiss = () => undefined;

/** Lets the hook's fetch and its `setImages` land before the test looks at what rendered. */
const settle = () => new Promise((resolve) => setImmediate(resolve));

/**
 * The row that owns a banner checks `availableImages.has(...)` before it renders one, so the set has
 * always landed by the time a `GameImage` first runs. Mounting that reader is what puts the test in
 * the same state the app is in.
 */
const loadAvailableImages = async (images, cacheBuster) => {
  setAvailable(images);
  ImageCacheContext.currentValue = cacheBuster;
  const row = mount(() => useAvailableGameImages());
  row.render();
  await settle();
  row.render();
  return row;
};

test('a banner is created at its own version, so one render pass makes one request', async () => {
  resetAvailableGameImages();
  await loadAvailableImages({ 730: 1756000000 }, 1);

  const view = mount(() => GameImage({ gameAppId: 730, alt: 'a game', onError: ignoreMiss }));
  view.render();

  // The very first src already names the stored version. An element given a versionless URL first
  // and the real one afterwards would download the same artwork twice on a cold page load.
  const firstPaint = view.output.props.src;
  assert.equal(firstPaint, '/api/game-images/730/header/1756000000');

  // Nothing arriving afterwards moves it, so there is no second request to make.
  await settle();
  view.render();
  assert.equal(view.output.props.src, firstPaint, 'the src is not rewritten after first paint');
});

test('an id advertised in a different case still counts as available', async () => {
  resetAvailableGameImages();
  // Epic keeps whatever case it gave an id, so a download row asks for "Fortnite" while the same
  // game is advertised as "fortnite". The row checks .has() before it renders anything, so a
  // case-sensitive answer hid a banner whose bytes the server was serving perfectly well. Games with
  // a hex-string id were unaffected, which is what made it look like only some titles broke.
  const row = await loadAvailableImages({ fortnite: 1756000000, abc123def: 1755000000 }, 42);

  assert.equal(row.output.has('Fortnite'), true, 'the row would otherwise render no banner at all');
  assert.equal(row.output.has('fortnite'), true, 'the advertised spelling still matches');
  assert.equal(row.output.has('abc123def'), true, 'an all-lower id is unaffected');
  assert.equal(row.output.has('NotStored'), false, 'an id nobody advertised is still absent');

  // The version has to survive the same mismatch, or the banner is requested at version 0 and the
  // browser is handed a URL it can never keep.
  assert.equal(row.output.versionOf('Fortnite'), 1756000000);

  const view = mount(() =>
    GameImage({ epicAppId: 'Fortnite', alt: 'Fortnite', onError: ignoreMiss })
  );
  view.render();
  // The URL keeps the id's own case, because that is the spelling the image route answers.
  assert.equal(view.output.props.src, '/api/game-images/epic/Fortnite/header/1756000000');
});

test('new artwork for one game changes only that banner url', async () => {
  resetAvailableGameImages();
  const row = await loadAvailableImages({ 730: 1756000000, 440: 1755000000 }, 10);

  const changed = mount(() => GameImage({ gameAppId: 730, alt: 'a game', onError: ignoreMiss }));
  const untouched = mount(() =>
    GameImage({ gameAppId: 440, alt: 'another game', onError: ignoreMiss })
  );
  changed.render();
  untouched.render();
  const untouchedSrc = untouched.output.props.src;

  // A fetch pass replaced one game's header and left the rest alone.
  setAvailable({ 730: 1756009999, 440: 1755000000 });
  ImageCacheContext.currentValue = 11;
  row.render();
  await settle();
  changed.render();
  untouched.render();
  await settle();
  changed.render();
  untouched.render();

  assert.equal(changed.output.props.src, '/api/game-images/730/header/1756009999');
  assert.equal(
    untouched.output.props.src,
    untouchedSrc,
    'a banner whose artwork did not change keeps the URL the browser already cached'
  );
});

test('a banner that failed to load comes back when its artwork is stored again', async () => {
  resetAvailableGameImages();
  const row = await loadAvailableImages({ 730: 1756000000 }, 20);

  const reportedMisses = [];
  const view = mount(() =>
    GameImage({ gameAppId: 730, alt: 'a game', onError: (key) => reportedMisses.push(key) })
  );
  view.render();
  assert.equal(view.output.type, 'img', 'the banner renders before anything fails');

  // The header request 404s because the cache was cleared out from under the loaded set.
  view.output.props.onError();
  view.render();
  assert.equal(view.output, null, 'a failed banner renders nothing');
  assert.deepEqual(reportedMisses, ['730']);

  // The fetch pass stored the artwork again, so this app's version moves and the failure is no
  // longer evidence of anything.
  setAvailable({ 730: 1756111111 });
  ImageCacheContext.currentValue = 21;
  row.render();
  await settle();
  view.render();
  await settle();
  view.render();
  assert.equal(view.output?.type, 'img', 'the banner retries once its version moves');
  assert.equal(view.output.props.src, '/api/game-images/730/header/1756111111');
});

const imageErrorsUrl = await compileToUrl('../src/hooks/useImageErrors.ts', {
  react: reactStubUrl,
  '@components/common/ImageCacheContext': imageCacheStubUrl
});
const { useImageErrors } = await import(imageErrorsUrl);

test('a row forgets an earlier banner failure once the image cache version moves', () => {
  ImageCacheContext.currentValue = 0;
  const view = mount(() => useImageErrors());
  view.render();

  // The header request 404s, so the row stops rendering the banner and draws its placeholder. The
  // component is gone from that point, which is why clearing its own flag cannot be enough.
  view.output.handleImageError('730');
  view.render();
  assert.equal(view.output.imageErrors.has('730'), true, 'the row remembers the failed banner');

  // The fetch pass stored the artwork and bumped the version, so the failure is no longer evidence.
  ImageCacheContext.currentValue = 1;
  view.render();
  assert.equal(view.output.imageErrors.has('730'), false, 'a version bump clears the failure');
});

test('the shared id set is reused while fresh, refetched when stale or bumped, and resettable', async () => {
  const realNow = Date.now;
  try {
    resetAvailableGameImages();
    setAvailable({ 730: 1 });
    ImageCacheContext.currentValue = 7;
    const before = callCount();

    const first = mount(() => useAvailableGameImages());
    first.render();
    await settle();
    assert.equal(callCount(), before + 1, 'the first mount fetches');

    // A second row mounts at the same version moments later and reads the loaded set.
    const second = mount(() => useAvailableGameImages());
    second.render();
    await settle();
    assert.equal(callCount(), before + 1, 'a mount inside the freshness window does not refetch');
    assert.ok(second.output.has('730'));

    // Half a minute passes with nothing bumping the version.
    const later = realNow() + 30_001;
    Date.now = () => later;
    const third = mount(() => useAvailableGameImages());
    third.render();
    await settle();
    assert.equal(callCount(), before + 2, 'a stale set is loaded again');

    // The backend fetched new art, so the version moves and the set is known to have changed.
    Date.now = realNow;
    ImageCacheContext.currentValue = 8;
    const fourth = mount(() => useAvailableGameImages());
    fourth.render();
    await settle();
    assert.equal(callCount(), before + 3, 'a new version fetches without waiting on freshness');

    resetAvailableGameImages();
    const fifth = mount(() => useAvailableGameImages());
    fifth.render();
    await settle();
    assert.equal(callCount(), before + 4, 'the reset drops the set the next mount would have used');
  } finally {
    Date.now = realNow;
  }
});

test('a response that arrives after a newer one does not put the older set back', async () => {
  resetAvailableGameImages();
  holdResponses();
  const before = callCount();

  // A depot mapping triggers a fetch pass that lands alongside the scheduled one, so a second
  // version bump arrives while the first request for the id set is still in flight.
  ImageCacheContext.currentValue = 20;
  const first = mount(() => useAvailableGameImages());
  first.render();
  await settle();

  ImageCacheContext.currentValue = 21;
  const second = mount(() => useAvailableGameImages());
  second.render();
  await settle();

  assert.equal(callCount(), before + 2, 'the newer version issues its own request');

  // The newer request answers first, then the older one comes back with what it saw.
  answer(1, { newer: 2 });
  await settle();
  answer(0, { older: 1 });
  await settle();

  // A component mounting now reads the shared set, and it must be the newer pass's.
  const reader = mount(() => useAvailableGameImages());
  reader.render();
  await settle();
  assert.ok(reader.output.has('newer'), 'the newer pass wins');
  assert.ok(!reader.output.has('older'), 'the older response must not overwrite it');
  assert.equal(callCount(), before + 2, 'the newer set is still fresh, so nothing refetches');
});
