import { useTranslation } from 'react-i18next';
import Badge from '@components/ui/Badge';

interface EvictedBadgeProps {
  className?: string;
}

export default function EvictedBadge({ className }: EvictedBadgeProps) {
  const { t } = useTranslation();
  return (
    <Badge variant="error" className={className}>
      {t('common.evicted')}
    </Badge>
  );
}
