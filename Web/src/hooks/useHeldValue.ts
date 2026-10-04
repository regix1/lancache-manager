import { useState } from 'react';

/**
 * The value while it is set, and the last value it had once it becomes null. A dialog that closes
 * when its subject is cleared keeps drawing that subject while it fades out.
 */
export function useHeldValue<T>(value: T | null): T | null {
  const [held, setHeld] = useState(value);
  if (value !== null && value !== held) setHeld(value);
  return value ?? held;
}
