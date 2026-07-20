import React from 'react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import type { CardNotice } from '@utils/cardDirectoryNotice';

const CardDirectoryNotice: React.FC<{ notice: CardNotice | null }> = ({ notice }) => {
  const { t } = useTranslation();

  if (notice === null) {
    return null;
  }

  return (
    <Alert color={notice.color} className="mb-6">
      <div>
        <p className="font-medium">{t(notice.titleKey)}</p>
        {notice.body.kind === 'ro' ? (
          <p className="text-sm mt-1">
            {t(notice.body.prefixKey)} <code className="bg-themed-tertiary px-1 rounded">:ro</code>{' '}
            {t(notice.body.suffixKey)}
          </p>
        ) : notice.body.kind === 'text' ? (
          <p className="text-sm mt-1">{t(notice.body.key)}</p>
        ) : (
          <p className="text-sm mt-1">{t(notice.body.messageKey)}</p>
        )}
      </div>
    </Alert>
  );
};

export default CardDirectoryNotice;
