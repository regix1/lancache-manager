/**
 * How long one sign-in attempt gets before the manager gives up on it, on every surface that runs
 * a login: the Configure card's persistent-container attempt, the in-process login behind the
 * Integrations card and the setup wizard, and the phone-approval wait inside a prefill container.
 *
 * One value rather than three that happen to agree, because the same number is both the timer that
 * ends the attempt and the number the countdown shows the person. Three constants drift, and a
 * countdown that outlives its own timer, or dies before it, is a lie either way.
 *
 * This is a ceiling, not a promise. Steam gives up on a mobile approval about two minutes in, and
 * that is Steam's clock, not one this repo owns, so an attempt can end well before the count
 * reaches zero. Every surface says so in the modal when it happens instead of just closing.
 */
export const LOGIN_ATTEMPT_TIMEOUT_MS = 10 * 60 * 1000;
