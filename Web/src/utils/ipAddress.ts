/**
 * Addresses as typed text. Shared by every surface that lets someone write an address by hand,
 * because they all take the same shape of input: one address or a pasted list of them, separated
 * by whatever the clipboard happened to carry.
 *
 * The checks are deliberately shape-only. They decide whether a string is worth sending, not
 * whether the address exists or belongs to this network; the server parses it properly and
 * normalises it, and its answer is what the screens go by.
 */

const isValidIpv4 = (value: string): boolean => {
  const parts = value.split('.');
  if (parts.length !== 4) return false;
  return parts.every((part) => {
    if (!/^\d{1,3}$/.test(part)) return false;
    const num = Number(part);
    return num >= 0 && num <= 255;
  });
};

const isValidIpv6 = (value: string): boolean => {
  if (!value.includes(':')) return false;
  if (!/^[0-9a-fA-F:]+$/.test(value)) return false;
  const parts = value.split(':');
  if (parts.length < 3 || parts.length > 8) return false;
  return parts.every((part) => part.length <= 4);
};

/**
 * Splits typed or pasted text into address candidates. Commas and any whitespace separate, so a
 * list copied out of a spreadsheet, a terminal or a config file all arrive the same way.
 */
export function parseIpCandidates(input: string): string[] {
  return input
    .split(/[,\s]+/)
    .map((candidate) => candidate.trim())
    .filter(Boolean);
}

/** Whether a single candidate is shaped like an address of either family. */
export function isValidIpAddress(value: string): boolean {
  return isValidIpv4(value) || isValidIpv6(value);
}

/**
 * Whether a candidate is shaped like a DNS name worth asking the resolver about: at least one dot
 * or letter to tell it apart from a half-typed address, no separators, and inside the length a
 * name is allowed. The server checks it again before querying.
 */
export function isPlausibleHostname(value: string): boolean {
  if (value.length === 0 || value.length > 253) return false;
  if (isValidIpAddress(value)) return false;
  if (
    !/^[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)*\.?$/.test(
      value
    )
  ) {
    return false;
  }
  return /[a-zA-Z]/.test(value);
}
