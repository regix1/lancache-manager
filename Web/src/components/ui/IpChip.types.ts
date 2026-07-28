/**
 * How a chip's address relates to the membership it is rendered in.
 *
 * - `current`   an address the group already has; the x marks it for removal
 * - `added`     an address chosen in this editing session; the x un-chooses it
 * - `removing`  a current address marked for removal; the x undoes that
 * - `readonly`  a glance-only address; no x is rendered
 */
export type IpChipState = 'current' | 'added' | 'removing' | 'readonly';
