const COOKIE_NAME = 'LancacheManager.Antiforgery';
const HEADER_NAME = 'X-Antiforgery-Token';

/**
 * The antiforgery header the server requires on every request that changes something.
 *
 * The value is read back out of a cookie the server writes beside the session status, and that cookie
 * is readable on purpose: a page served by another origin cannot read this origin's cookies, so it
 * cannot produce this header, so the request it forges is refused. The session cookie itself stays
 * out of reach of script and is never read here.
 *
 * Empty until the first status call has answered, which is why it returns an object to spread rather
 * than a string: a header carrying nothing is worse than no header, because it reads as a token that
 * failed rather than one that has not arrived.
 */
export function antiforgeryHeaders(): Record<string, string> {
  const prefix = `${COOKIE_NAME}=`;
  const entry = document.cookie.split('; ').find((candidate) => candidate.startsWith(prefix));

  // Cookie values are percent-encoded on the way out, so decoding is undoing the server's own step.
  return entry ? { [HEADER_NAME]: decodeURIComponent(entry.slice(prefix.length)) } : {};
}
