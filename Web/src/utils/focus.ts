const FOCUSABLE_SELECTOR =
  'a[href],area[href],input:not([disabled]),select:not([disabled]),textarea:not([disabled]),' +
  'button:not([disabled]),summary,iframe,object,embed,[tabindex]:not([tabindex="-1"]),[contenteditable="true"]';

export const getFocusable = (el: HTMLElement): HTMLElement[] => {
  const controls = Array.from(el.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter(
    (n) => n.offsetParent !== null
  );
  return controls.filter((control) => {
    if (!(control instanceof HTMLInputElement) || control.type !== 'radio' || !control.name) {
      return true;
    }
    const group = controls.filter(
      (candidate): candidate is HTMLInputElement =>
        candidate instanceof HTMLInputElement &&
        candidate.type === 'radio' &&
        candidate.name === control.name &&
        candidate.form === control.form
    );
    return control === (group.find((candidate) => candidate.checked) ?? group[0]);
  });
};
