import { createContext, useContext, useEffect } from 'react';

interface AccordionGroupContextValue {
  register: (id: string, isExpanded: boolean, onToggle: () => void) => void;
  unregister: (id: string) => void;
  expandAll: () => void;
  collapseAll: () => void;
  /** True when hasItems is true AND at least one registered item is expanded, so one click can collapse a mixed group. */
  anyExpanded: boolean;
  hasItems: boolean;
}

export const AccordionGroupContext = createContext<AccordionGroupContextValue | undefined>(
  undefined
);

/** Syncs one AccordionSection's expand state into the nearest AccordionGroupProvider. No-op with no provider in the tree. */
export function useAccordionGroupItem(id: string, isExpanded: boolean, onToggle: () => void): void {
  const ctx = useContext(AccordionGroupContext);
  useEffect(() => {
    if (!ctx) return;
    ctx.register(id, isExpanded, onToggle);
    return () => ctx.unregister(id);
  }, [ctx, id, isExpanded, onToggle]);
}

export function useAccordionGroupControls(): AccordionGroupContextValue | undefined {
  return useContext(AccordionGroupContext);
}
