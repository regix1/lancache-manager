import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, declaredValue, liftConstArrow } from './transpile-module.mjs';

/**
 * Picking a theme on your own session stores it on the server, then downloads the theme file to
 * repaint the page. When only that download fails the server already holds the new theme, so the
 * row has to keep showing it and the pane must not say the save failed. The shipped `writePreference`,
 * the step the pane's Save runs per row, runs against a server that stores every write and a theme
 * download that fails.
 */

const SESSION_PREFERENCES = 'src/components/features/user/SessionPreferences.tsx';
const SAVE_FAILED_KEY = 'activeSessions.errors.savePreferences';

/** Lets every pending `.then`/`await` continuation run. */
const settle = () => new Promise((resolve) => setImmediate(resolve));

/** A React state slot whose setter takes a value or an updater, as `useState`'s does. */
const slot = (initial) => {
  const box = { value: initial };
  box.set = (next) => {
    box.value = typeof next === 'function' ? next(box.value) : next;
  };
  return box;
};

/** The viewer's own preferences pane over a server that stores each PUT and answers each GET. */
const ownPane = (themeDownload) => {
  const stored = declaredValue('src/types/userPreferences.ts', 'DEFAULT_PREFERENCES');
  const server = { current: { ...stored, selectedTheme: 'dark-default' } };
  const shown = slot({ ...server.current });
  const notices = [];
  const bindings = {
    session: { id: 'own-session', isCurrentSession: true, sessionType: 'admin' },
    writeChainRef: { current: Promise.resolve() },
    confirmedRef: { current: { ...server.current } },
    pendingRef: { current: new Map() },
    pendingTotalRef: { current: 0 },
    clickCountRef: { current: 0 },
    reloadOwedRef: { current: false },
    reloadPrefs: async () => undefined,
    setPrefs: shown.set,
    setSavingField: () => undefined,
    ApiService: {
      getSessionPreferences: async () => ({ ...server.current }),
      saveSessionPreferences: async (_id, body) => {
        server.current = { ...body };
      },
      setSessionRefreshRate: async () => undefined
    },
    themeService: {
      setTheme: themeDownload,
      setSharpCorners: async () => true,
      setDisableTooltips: async () => true,
      setPicsAlwaysVisible: async () => true,
      setDisableStickyNotifications: async () => true
    },
    notifyError: (message, error) => notices.push({ message, error }),
    t: (key) => key
  };
  // The row's write runs in `writePreference`; a rejection is reported the way the pane's Save does.
  bindings.writePreference = bindLifted(
    liftConstArrow(SESSION_PREFERENCES, 'writePreference'),
    bindings
  );
  const savePreference = (key, value) =>
    void bindings
      .writePreference(key, value)
      .catch((error) => bindings.notifyError(SAVE_FAILED_KEY, error));
  return { server, shown, notices, savePreference };
};

/** The two ways the theme file download fails: the server answers an error, or the network drops. */
const downloadFailures = [
  ['the theme file read answers 500', async () => Promise.reject(new Error('HTTP 500'))],
  [
    'the theme file read loses the network',
    async () => Promise.reject(new TypeError('Failed to fetch'))
  ]
];

for (const [name, themeDownload] of downloadFailures) {
  test(`own theme saved while ${name}: the row keeps the new theme and no save failure shows`, async () => {
    const pane = ownPane(themeDownload);

    pane.savePreference('selectedTheme', 'custom-ocean');
    await settle();
    await settle();

    assert.equal(pane.server.current.selectedTheme, 'custom-ocean', 'the server stored the pick');
    assert.equal(
      pane.shown.value.selectedTheme,
      'custom-ocean',
      'the theme row went back to the old theme although the server holds the new one'
    );
    assert.deepEqual(
      pane.notices
        .filter((notice) => notice.message === SAVE_FAILED_KEY)
        .map((notice) => notice.message),
      [],
      'the pane reported the theme save as failed although the server stored it'
    );
  });
}
