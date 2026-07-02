import { useTranslation } from 'react-i18next';
import Badge from '@components/ui/Badge';

interface PrefillBadgeProps {
  className?: string;
}

export default function PrefillBadge({ className }: PrefillBadgeProps) {
  const { t } = useTranslation();
  return (
    <Badge variant="neutral" className={className}>
      {t('common.prefill')}
    </Badge>
  );
}
