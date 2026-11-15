import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, declaredValue, liftConstArrow } from './transpile-module.mjs';

/**
 * Apple signs in only through an HTTPS domain address, and the server refuses an Apple sign-in start
 * on `localhost` or an IP address even over HTTPS (AccessController.cs, the Apple clause of the
 * address check). The access settings page marks a service it cannot use there with the HTTPS badge
 * and blocks saving it, through `httpsBlocks`. These cases run the shipped `httpsBlocks` against the
 * page address the browser reports.
 */

const ACCESS = 'src/components/initialization/AccessSetup.tsx';

/** The shipped `https` value and `httpsBlocks` for a page loaded from `href`. */
const blocksOn = (href) => {
  const url = new URL(href);
  const window = {
    location: { protocol: url.protocol, hostname: url.hostname, host: url.host, href },
    // The browser counts localhost and HTTPS pages as secure contexts.
    isSecureContext: url.protocol === 'https:' || url.hostname === 'localhost'
  };
  const https = declaredValue(ACCESS, 'https', { window });
  return bindLifted(liftConstArrow(ACCESS, 'httpsBlocks'), { https, window });
};

test('the access settings page blocks Apple on an HTTPS page served from localhost or an IP address', () => {
  const wrong = [];
  for (const href of [
    'https://192.168.1.20/',
    'https://192.168.1.20:8443/',
    'https://localhost/',
    'https://localhost:5443/'
  ]) {
    if (blocksOn(href)('apple') !== true)
      wrong.push(`on ${href} the page offers Apple, which the server refuses there`);
  }
  assert.deepEqual(wrong, [], wrong.join('; '));

  // The cases the server accepts or already refuses stay as they are.
  assert.equal(
    blocksOn('https://lancache.example.com/')('apple'),
    false,
    'Apple is blocked on an HTTPS domain'
  );
  assert.equal(
    blocksOn('http://lancache.example.com/')('apple'),
    true,
    'Apple is offered over plain HTTP'
  );
  assert.equal(
    blocksOn('https://192.168.1.20/')('github'),
    false,
    'GitHub is blocked on an HTTPS address'
  );
});
