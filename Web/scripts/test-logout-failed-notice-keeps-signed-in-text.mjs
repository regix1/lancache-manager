import assert from 'node:assert/strict';
import test from 'node:test';
import { bareAnswer, loadAuthService, noAnswer } from './sign-out-auth-service.mjs';

/**
 * With authentication on, a sign-out that the server refused or never got leaves the window signed
 * in: the page stays as it was and the next status read finds the session still live. A 500 with no
 * code came before the sign-out ran, so the cookie is still set. The notice is the only thing that
 * tells the user, so for none of these answers may it say this device is signed out.
 */

const FAILED = [
  { answer: 'no answer', respond: noAnswer },
  { answer: '400 from the antiforgery check', respond: () => bareAnswer(400) },
  { answer: '500 from the server', respond: () => bareAnswer(500) },
  { answer: '502 from a proxy', respond: () => bareAnswer(502) },
  { answer: '503 from a proxy', respond: () => bareAnswer(503) },
  { answer: '504 from a proxy', respond: () => bareAnswer(504) }
];

/** The notices one sign-out raises in `language` when its request gets `respond`. */
const noticesAfterFailedSignOut = async (language, respond) => {
  const raised = [];
  globalThis.window = {
    dispatchEvent: (event) => {
      raised.push(event.detail);
      return true;
    }
  };
  globalThis.fetch = async () => respond();
  const originalError = console.error;
  console.error = () => undefined;
  try {
    const authService = await loadAuthService(language);
    assert.equal(await authService.logout(), 'unchanged');
  } finally {
    console.error = originalError;
    delete globalThis.window;
    delete globalThis.fetch;
  }
  return raised;
};

const CLAIMS_SIGNED_OUT = { en: /signed out on this device/i, zh: /已在此设备上登出/ };

for (const { answer, respond } of FAILED) {
  test(`a sign-out that got ${answer} does not say this device is signed out`, async () => {
    for (const language of ['en', 'zh']) {
      const notices = await noticesAfterFailedSignOut(language, respond);
      assert.equal(
        notices.length,
        1,
        `in ${language}, a sign-out that got ${answer} raised no notice`
      );
      const [{ type, message }] = notices;
      assert.equal(type, 'error');
      assert.doesNotMatch(message, new RegExp(`^no ${language} text`), message);
      assert.doesNotMatch(
        message,
        CLAIMS_SIGNED_OUT[language],
        `in ${language}, a sign-out that got ${answer} shows "${message}" while the window stays signed in`
      );
    }
  });
}
