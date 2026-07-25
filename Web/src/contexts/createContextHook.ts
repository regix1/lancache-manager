import { useContext, type Context } from 'react';

/**
 * Builds the "read this context, throw outside its provider" hook that every context in this folder
 * needs. The context is taken whole rather than as `Context<T | undefined | null>`: React's Context<T>
 * is invariant in T through its Provider props, so a widened parameter rejects every real context.
 * `name` is the consuming hook's own name and appears in the error, so a missing provider reports the
 * hook the caller actually used rather than this factory.
 */
export function createContextHook<T>(context: Context<T>, name: string): () => NonNullable<T> {
  return function useContextValue(): NonNullable<T> {
    const value = useContext(context);
    if (!value) {
      throw new Error(`${name} must be used within its provider`);
    }
    return value;
  };
}
