import { useTranslation } from 'react-i18next';
import { useAccordionGroupControls } from '@contexts/AccordionGroupContext';

/** Page-level expand/collapse-all control for every AccordionSection registered on the current page. Renders nothing until at least one section has registered. */
export function AccordionGroupToggle() {
  const ctx = useAccordionGroupControls();
  const { t } = useTranslation();

  if (!ctx || !ctx.hasItems) return null;

  return (
    <button
      type="button"
      className="mb-3 min-h-8 px-3 py-1.5 text-sm font-medium whitespace-nowrap themed-border-radius-sm bg-themed-surface hover:bg-themed-surface-hover text-themed-primary smooth-transition button-press"
      onClick={ctx.anyExpanded ? ctx.collapseAll : ctx.expandAll}
    >
      {ctx.anyExpanded
        ? t('management.gameDetection.collapseAll')
        : t('management.gameDetection.expandAll')}
    </button>
  );
}
