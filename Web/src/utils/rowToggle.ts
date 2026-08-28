import type { MouseEvent, KeyboardEvent } from 'react';

interface RowToggleHandlers {
  role: 'button';
  tabIndex: number;
  onClick: (event: MouseEvent) => void;
  onKeyDown: (event: KeyboardEvent) => void;
}

/**
 * role/tabIndex/onClick/onKeyDown for a row that toggles on click or Enter/Space, while letting a
 * nested real control (a button, link, checkbox, etc.) handle its own activation without also
 * toggling the row. `onToggle` is the row's own toggle - callers close over whatever key identifies
 * their row (a service name, a session id, ...).
 */
export const rowToggleHandlers = (onToggle: () => void): RowToggleHandlers => {
  const fromNestedControl = (target: EventTarget | null, currentTarget: EventTarget) => {
    // Element, not HTMLElement: an icon inside a nested control is an SVGElement, which inherits
    // from Element and not from HTMLElement. Narrowing to HTMLElement here made every click that
    // landed on an icon look like a click on bare row background, so the row toggled underneath
    // the control the user actually pressed.
    if (!(target instanceof Element) || !(currentTarget instanceof HTMLElement)) return false;
    const control = target.closest(
      'button, input, a, label, [role="button"], [role="checkbox"], [role="listbox"], [role="combobox"]'
    );
    return control !== null && control !== currentTarget;
  };
  return {
    role: 'button' as const,
    tabIndex: 0,
    onClick: (event: MouseEvent) => {
      if (!fromNestedControl(event.target, event.currentTarget)) onToggle();
    },
    onKeyDown: (event: KeyboardEvent) => {
      if (
        (event.key === 'Enter' || event.key === ' ') &&
        !fromNestedControl(event.target, event.currentTarget)
      ) {
        event.preventDefault();
        onToggle();
      }
    }
  };
};
