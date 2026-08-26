import { useEffect, useState, type Dispatch, type SetStateAction } from 'react';
import { storage } from '@utils/storage';

/**
 * Whether a management section is open, remembered across reloads.
 *
 * Eight sections each kept their own copy of this: an initializer reading the key, and an effect
 * writing `String(expanded)` back on every change. They differ in the key and in what an unset key
 * means, so those are the two arguments and there is nothing else to vary.
 *
 * The read goes through the storage wrapper because the initializer runs during render. A browser
 * with site data blocked for the origin throws on the `localStorage` property access itself, and an
 * uncaught throw in a render takes out the whole section rather than one preference.
 */
export function useSectionExpanded(
  key: string,
  defaultExpanded: boolean
): [boolean, Dispatch<SetStateAction<boolean>>] {
  const [expanded, setExpanded] = useState(() => {
    const saved = storage.getItem(key);
    return saved !== null ? saved === 'true' : defaultExpanded;
  });

  useEffect(() => {
    storage.setItem(key, String(expanded));
  }, [key, expanded]);

  return [expanded, setExpanded];
}
