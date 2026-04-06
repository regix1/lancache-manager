import { useTranslation } from 'react-i18next';
import { getServiceBadgeStyles } from '@utils/serviceColors';
import Badge from '@components/ui/Badge';
import EvictedBadge from '@components/common/EvictedBadge';

interface BadgesRowProps {
  service: string;
  datasource?: string;
  showDatasource?: boolean;
  isEvicted?: boolean;
  isPartiallyEvicted?: boolean;
  className?: string;
}

export default function BadgesRow({
  service,
  datasource,
  showDatasource,
  isEvicted,
  isPartiallyEvicted,
  className
}: BadgesRowProps) {
  const { t } = useTranslation();
  return (
    <div className={`flex items-center gap-1.5 flex-wrap${className ? ` ${className}` : ''}`}>
      <span className="themed-badge" style={getServiceBadgeStyles(service)}>
        {service.toUpperCase()}
      </span>
      {showDatasource && datasource && <Badge variant="neutral">{datasource}</Badge>}
      {isEvicted && <EvictedBadge />}
      {isPartiallyEvicted && <Badge variant="warning">{t('common.partiallyEvicted')}</Badge>}
    </div>
  );
}
