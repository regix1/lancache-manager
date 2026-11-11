import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

const hookPath = 'src/components/features/prefill/hooks/usePrefillSignalR.ts';
const noop = () => undefined;

// Runs the mount-time session lookup against a connection whose subscribe call throws an error
// with arbitrary text, and reports whether the connection box with Retry was shown.
const showsConnectionBox = async (connectToHub) => {
  let failed = false;
  const initializeSession = bindLifted(
    liftHookCallback(hookPath, 'useCallback', "'GetMySessions'"),
    {
      initializationAttempted: { current: false },
      setHubConnectFailed: (value) => {
        failed = value;
      },
      setIsInitializing: noop,
      connectToHub,
      addLog: noop,
      t: (key) => key,
      HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
      getErrorMessage: (error) => String(error),
      console: { error: noop, warn: noop, log: noop }
    }
  );
  await initializeSession();
  return failed;
};

const connectionIn = (state) => ({
  state,
  invoke: async (name) => {
    if (name === 'GetMySessions') {
      return [{ id: 'session-a', status: 'Active', authState: 'Authenticated' }];
    }
    throw new Error('any text at all');
  }
});

test('an error from a connection that is no longer connected is an expected close, whatever its text', async () => {
  assert.equal(await showsConnectionBox(async () => connectionIn('Disconnected')), false);
});

test('an error from a connection that is still connected shows the connection box', async () => {
  assert.equal(await showsConnectionBox(async () => connectionIn('Connected')), true);
});

test('a throw before any connection exists shows the connection box', async () => {
  assert.equal(
    await showsConnectionBox(async () => {
      throw new Error('any text at all');
    }),
    true
  );
});
