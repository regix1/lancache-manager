import React from 'react';
import { useTranslation } from 'react-i18next';
import FormField from './FormField';
import type { CredentialFieldsProps } from './CredentialFields.types';
import { requiresApiKey, usesOidc } from '@utils/accountMode';

const inputClassName = 'w-full p-3 text-sm themed-input';

/**
 * The API key, username and password block that every sign-in surface renders.
 *
 * Three copies of it drifted apart: one screen rendered the API key as plain text while the other
 * two masked it, so the same secret was readable over a shoulder on one screen and not the others.
 * The key owns the whole installation, so it is masked here the way a password is, once, for every
 * caller.
 *
 * Enter lives here too. Each copy carried its own key handler that submitted only once all three
 * fields were filled, and each checked a differently named loading flag. A disabled input receives
 * no keyboard events, so `disabled` is the whole guard and the fill check is all that is left.
 */
const CredentialFields: React.FC<CredentialFieldsProps> = ({
  apiKey,
  username,
  password,
  onChange,
  onSubmit,
  disabled,
  apiKeyPlaceholder,
  autoFocus,
  accountMode = 'apiKeyPassword'
}) => {
  const { t } = useTranslation();

  const keyRequired = requiresApiKey(accountMode);
  const oidc = usesOidc(accountMode);
  const credentialsFilled =
    (!keyRequired || apiKey.trim() !== '') && (oidc || (username.trim() !== '' && password !== ''));

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      if (credentialsFilled) onSubmit();
    }
  };

  return (
    <>
      {keyRequired && (
        <div>
          <FormField label={t('modals.auth.labels.apiKey')}>
            {(field) => (
              <input
                {...field}
                type="password"
                value={apiKey}
                onChange={(e) => onChange('apiKey', e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder={apiKeyPlaceholder}
                className={inputClassName}
                // `new-password` rather than `off`: browsers ignore `off` on a password input and
                // will still offer to remember the key and refill it on a later visit. The username
                // and password fields below are a real sign-in, so they keep their own semantics.
                autoComplete="new-password"
                disabled={disabled}
                autoFocus={autoFocus}
              />
            )}
          </FormField>
        </div>
      )}

      {!oidc && (
        <>
          <div>
            <FormField label={t('modals.auth.labels.username')}>
              {(field) => (
                <input
                  {...field}
                  type="text"
                  value={username}
                  onChange={(e) => onChange('username', e.target.value)}
                  onKeyDown={handleKeyDown}
                  placeholder={t('modals.auth.placeholders.enterUsername')}
                  className={inputClassName}
                  autoComplete="username"
                  disabled={disabled}
                  autoFocus={autoFocus && !keyRequired}
                />
              )}
            </FormField>
          </div>

          <div>
            <FormField label={t('modals.auth.labels.password')}>
              {(field) => (
                <input
                  {...field}
                  type="password"
                  value={password}
                  onChange={(e) => onChange('password', e.target.value)}
                  onKeyDown={handleKeyDown}
                  placeholder={t('modals.auth.placeholders.enterPassword')}
                  className={inputClassName}
                  autoComplete="current-password"
                  disabled={disabled}
                />
              )}
            </FormField>
          </div>
        </>
      )}
    </>
  );
};

export default CredentialFields;
