/** The label a row shows for a client address, plus whether it stands in for that address. */
interface ClientLabel {
  /** What the row shows: nickname, else short hostname, else the raw address. */
  text: string;
  /** True when text is standing in for the address, so callers add the dashed-underline affordance. */
  substitutesAddress: boolean;
}

/**
 * One precedence for every client label in the app: nickname > hostname > raw address.
 * The short hostname (text before the first dot) is used because reverse-DNS answers on a LAN are
 * usually fully qualified and the rows have no horizontal room for a full name. The server applies
 * the same short-name rule to the display name it computes, so both surfaces agree.
 */
export function resolveClientLabel(
  clientIp: string,
  nickname: string | null | undefined,
  hostname: string | null | undefined
): ClientLabel {
  // A blank-but-present value counts as absent, matching the rule the server applies to the display
  // name it computes. Without this a nickname of only spaces renders as an empty label here while
  // the server-labelled tables show a hostname.
  if (nickname?.trim()) {
    return { text: nickname, substitutesAddress: true };
  }
  if (hostname?.trim()) {
    const shortName = hostname.split('.')[0];
    return { text: shortName || hostname, substitutesAddress: true };
  }
  return { text: clientIp, substitutesAddress: false };
}
