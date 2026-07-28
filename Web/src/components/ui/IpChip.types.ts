/**
 * How a chip's address relates to the membership it is rendered in.
 *
 * - `current`   an address the group already has; the x marks it for removal
 * - `added`     an address chosen in this editing session; the x un-chooses it
 * - `removing`  a current address marked for removal; the x undoes that
 * - `readonly`  a glance-only address; no x is rendered
 */
export type IpChipState = 'current' | 'added' | 'removing' | 'readonly';

export interface IpChipProps {
  address: string;
  state: IpChipState;
  /** Omit for a glance-only chip; no remove control is rendered without it. */
  onRemove?: () => void;
  /** Tooltip and screen-reader wording for the remove control. Defaults to "Remove". */
  removeLabel?: string;
  /**
   * The whole accessible name for the remove control. Supply it where the label is prose, which
   * reads as a sentence fragment once a verb is put in front of it. [54]
   */
  removeAriaLabel?: string;
  /** Blocks the remove control while a save is in flight. */
  disabled?: boolean;
  /**
   * Set false where the chip carries a readable label instead of an address, so the digit
   * alignment a monospace face buys on an address is not spent on prose. [39]
   */
  mono?: boolean;
  /**
   * What the hover tooltip shows. Defaults to `address`, so a chip whose `address` prop has been
   * swapped for a name can still put the raw address on hover. [13]
   */
  tooltip?: string;
  /**
   * A short qualifier shown after the label, for a chip whose address needs a word of explanation
   * to be read correctly, such as one typed in by hand that no download has ever come from. [63]
   */
  note?: string;
  className?: string;
}
