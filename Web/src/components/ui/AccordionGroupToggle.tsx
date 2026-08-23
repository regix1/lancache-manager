import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { useAccordionGroupControls } from '@contexts/AccordionGroupContext';

/** Page-level expand/collapse-all control for every AccordionSection registered on the current page. Renders nothing until at least one section has registered. */
export function AccordionGroupToggle() {
  const ctx = useAccordionGroupControls();
  const { t } = useTranslation();

  if (!ctx || !ctx.hasItems) return null;

  return (
    <Button
      type="button"
      variant="default"
      size="sm"
      onClick={ctx.anyExpanded ? ctx.collapseAll : ctx.expandAll}
    >
      {ctx.anyExpanded
        ? t('management.gameDetection.collapseAll')
        : t('management.gameDetection.expandAll')}
    </Button>
  );
}
