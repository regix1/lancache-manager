/**
 * How long one sign-in attempt gets before the manager gives up on it, on every surface that runs
 * a login: the Configure card's persistent-container attempt, the in-process login behind the
 * Integrations card and the setup wizard, and the device-code wait inside a prefill container.
 *
 * One value per service rather than one per timer, because the same number is both the timer that
 * ends the attempt and the number the countdown shows the person. Separate constants drift, and a
 * countdown that outlives its own timer, or dies before it, is a lie either way.
 *
 * Epic gets five minutes because its daemon stops waiting five minutes after handing out the
 * authorization URL and then refuses the pasted code outright, so a longer count would keep ticking
 * past the point where pasting still works. Our clock arms a little earlier than the daemon's,
 * before the call that fetches the challenge returns, so it always expires first. Everything else
 * gets the generic window: no other daemon tells the browser when its own attempt ends.
 *
 * The id is compared without case because the two spellings in this app differ. The persistent
 * flows say 'Epic' and the prefill panel says 'epic'.
 */
export function loginAttemptTimeoutMs(service: string): number {
  return service.toLowerCase() === 'epic' ? 5 * 60 * 1000 : 10 * 60 * 1000;
}

/**
 * How long Steam waits for a phone approval before it abandons the sign-in. That is Steam's clock,
 * not one this repo owns, and it is a separate number from the attempt windows above because it
 * starts when the approval reaches the phone rather than when the person clicks Log in. Only the
 * surfaces that arm a timer on the approval itself use it. The Configure card's clock covers a
 * whole attempt, typing included, so it keeps the attempt window.
 */
export const STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS = 2 * 60 * 1000;
