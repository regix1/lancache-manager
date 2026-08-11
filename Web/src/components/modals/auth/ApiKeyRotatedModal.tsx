import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Key } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';

interface ApiKeyRotatedModalProps {
  opened: boolean;
  /**
   * Held by the caller from the rotation's answer and shown straight from there. Nothing can ask for
   * it again: the request that produced it ended every session, the caller's own included, so this
   * dialog is the only place the new key ever appears.
   */
  apiKey: string;
  onClose: () => void;
}

/**
 * The one showing of a freshly rotated API key. Closing it is the point of no return for whoever did
 * not copy the key, which is why the copy control sits next to it and the warning sits above it.
 */
export const ApiKeyRotatedModal: React.FC<ApiKeyRotatedModalProps> = ({
  opened,
  apiKey,
  onClose
}) => {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(apiKey);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard permission refused, or a page served over plain http where the API is absent. The
      // key stays on screen to be selected by hand, which is the only other way to take it.
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={
        <div className="flex items-center space-x-3">
          <Key className="w-6 h-6 text-themed-warning" />
          <span>{t('management.auth.rotatedModal.title')}</span>
        </div>
      }
    >
      <div className="space-y-4">
        <Alert color="yellow">{t('management.auth.rotatedModal.message')}</Alert>

        <code className="well-surface rotated-api-key">{apiKey}</code>

        <div className="flex flex-col-reverse sm:flex-row justify-end gap-3 pt-2">
          <Button variant="default" onClick={onClose} className="w-full sm:w-auto">
            {t('common.close')}
          </Button>
          <Button variant="filled" color="green" onClick={handleCopy} className="w-full sm:w-auto">
            {copied ? t('management.auth.rotatedModal.copied') : t('common.copy')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};
