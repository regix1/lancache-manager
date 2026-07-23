import { useCallback, useMemo, useReducer, useRef, type ReactNode } from 'react';
import { AccordionGroupContext } from '@contexts/AccordionGroupContext';

interface AccordionGroupProviderProps {
  children: ReactNode;
}

/** Registry entry for one AccordionSection synced via useAccordionGroupItem. */
interface AccordionGroupEntry {
  isExpanded: boolean;
  onToggle: () => void;
}

export function AccordionGroupProvider({ children }: AccordionGroupProviderProps) {
  const registryRef = useRef(new Map<string, AccordionGroupEntry>());
  const [, forceRender] = useReducer((c: number) => c + 1, 0);

  const register = useCallback((id: string, isExpanded: boolean, onToggle: () => void) => {
    const existing = registryRef.current.get(id);
    registryRef.current.set(id, { isExpanded, onToggle });
    // Only force a render when isExpanded actually changed - callers aren't required to
    // memoize onToggle, so re-rendering on every registration call (a fresh onToggle
    // identity each render) would re-run the registering effect, call register again,
    // force another render, and loop forever. Comparing isExpanded breaks that loop while
    // still keeping the stored onToggle reference current for expandAll/collapseAll.
    if (!existing || existing.isExpanded !== isExpanded) forceRender();
  }, []);

  const unregister = useCallback((id: string) => {
    if (registryRef.current.delete(id)) forceRender();
  }, []);

  const expandAll = useCallback(() => {
    registryRef.current.forEach((entry) => {
      if (!entry.isExpanded) entry.onToggle();
    });
  }, []);

  const collapseAll = useCallback(() => {
    registryRef.current.forEach((entry) => {
      if (entry.isExpanded) entry.onToggle();
    });
  }, []);

  const items = Array.from(registryRef.current.values());
  const hasItems = items.length > 0;
  const allExpanded = hasItems && items.every((item) => item.isExpanded);

  const value = useMemo(
    () => ({ register, unregister, expandAll, collapseAll, allExpanded, hasItems }),
    [register, unregister, expandAll, collapseAll, allExpanded, hasItems]
  );

  return <AccordionGroupContext.Provider value={value}>{children}</AccordionGroupContext.Provider>;
}
