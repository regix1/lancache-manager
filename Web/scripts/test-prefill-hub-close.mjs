import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

const hookPath = 'src/components/features/prefill/hooks/usePrefillSignalR.ts';
const noop = () => undefined;

// Runs the mount-time session lookup against a connection whose subscribe call throws, and reports
// whether the connection box with Retry was shown and which errors were logged.
const showsConnectionBox = async (connectToHub) => {
  let failed = false;
  const logged = [];
  const initializeSession = bindLifted(
    liftHookCallback(hookPath, 'useCallback', "'GetMySessions'"),
    {
      initializationAttempted: { current: false },
      acceptedConnectionRef: { current: null },
      setHubConnectFailed: (value) => {
        failed = value;
      },
      setIsInitializing: noop,
      connectToHub,
      addLog: noop,
      t: (key) => key,
      HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
      getErrorMessage: (error) => String(error),
      console: {
        error: (...args) => {
          logged.push(args.join(' '));
        },
        warn: noop,
        log: noop
      }
    }
  );
  await initializeSession();
  return { failed, logged };
};

const connectionIn = (state) => {
  const connection = {
    state,
    invoked: [],
    invoke: async (name) => {
      connection.invoked.push(name);
      if (name === 'GetMySessions') {
        return [{ id: 'session-a', status: 'Active', authState: 'Authenticated' }];
      }
      throw new Error('subscribe refused');
    }
  };
  return connection;
};

test('an error from a connection that is no longer connected is an expected close, whatever its text', async () => {
  const connection = connectionIn('Disconnected');
  const { failed, logged } = await showsConnectionBox(async () => connection);
  assert.deepEqual(
    connection.invoked,
    ['GetMySessions', 'SubscribeToSessionAsync'],
    'the lookup stopped before it subscribed to the running session'
  );
  assert.deepEqual(logged, [], 'an expected close was logged as a failure');
  assert.equal(failed, false);
});

test('an error from a connection that is still connected shows the connection box', async () => {
  const connection = connectionIn('Connected');
  const { failed, logged } = await showsConnectionBox(async () => connection);
  assert.equal(logged.length, 1, `expected one logged failure, got ${JSON.stringify(logged)}`);
  assert.match(
    logged[0],
    /subscribe refused/,
    `the logged failure is not the subscribe error: ${logged[0]}`
  );
  assert.equal(failed, true);
});

test('a throw before any connection exists shows the connection box', async () => {
  const { failed, logged } = await showsConnectionBox(async () => {
    throw new Error('connect refused');
  });
  assert.equal(logged.length, 1, `expected one logged failure, got ${JSON.stringify(logged)}`);
  assert.match(
    logged[0],
    /connect refused/,
    `the logged failure is not the connect error: ${logged[0]}`
  );
  assert.equal(failed, true);
});
