/**
 * Creates a global preference state with getter and setter.
 * Useful for preferences that need to be accessed without circular dependencies.
 * 
 * @template T The type of the preference value
 * @param defaultValue The initial value for the preference
 * @returns An object with get and set methods for managing the preference
 * 
 * @example
 * ```typescript
 * const myPreference = createGlobalPreference<boolean>(false);
 * 
 * // Later in your code
 * myPreference.set(true);
 * const value = myPreference.get(); // true
 * ```
 */
export function createGlobalPreference<T>(defaultValue: T) {
  let value = defaultValue;
  return {
    get: (): T => value,
    set: (newValue: T): void => { value = newValue; }
  };
}
