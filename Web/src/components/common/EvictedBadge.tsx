import React from 'react';
import { useTranslation } from 'react-i18next';

/**
 * Reusable badge component for displaying evicted status.
 * Renders the themed error badge with localized "Evicted" text.
 * Use alongside `opacity-60` on the parent row when the item is evicted.
 */
const EvictedBadge: React.FC = () => {
  const { t } = useTranslation();
  return <span className="themed-badge status-badge-error">{t('common.evicted')}</span>;
};

export default EvictedBadge;
