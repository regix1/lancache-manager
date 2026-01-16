import React, { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Languages } from 'lucide-react';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import i18n, { setLanguage } from '../../i18n';

const LANGUAGE_LABELS: Record<string, { label: string; shortLabel: string }> = {
  en: { label: 'English', shortLabel: 'EN' }
};

const getSupportedLanguages = (): string[] => {
  const supported = i18n.options.supportedLngs;
  const fallback = Object.keys(i18n.options.resources ?? {});
  const normalized = Array.isArray(supported) ? supported : fallback;
  return normalized.filter(
    (lang): lang is string => typeof lang === 'string' && lang !== 'cimode'
  );
};

const normalizeLanguage = (value: string, supported: string[]): string => {
  if (supported.includes(value)) return value;
  const base = value.split('-')[0];
  if (supported.includes(base)) return base;
  return supported[0] || value;
};

const getLanguageLabel = (code: string) => {
  return LANGUAGE_LABELS[code]?.label ?? code.toUpperCase();
};

const getLanguageShortLabel = (code: string) => {
  return LANGUAGE_LABELS[code]?.shortLabel ?? code.split('-')[0].toUpperCase();
};

const LanguageSelector: React.FC = () => {
  const { t, i18n: i18nInstance } = useTranslation();
  const supportedLanguages = useMemo(() => getSupportedLanguages(), []);
  const currentLanguage = normalizeLanguage(
    i18nInstance.resolvedLanguage || i18nInstance.language,
    supportedLanguages
  );

  const options = supportedLanguages.map((code) => ({
    value: code,
    label: getLanguageLabel(code),
    shortLabel: getLanguageShortLabel(code),
    icon: Languages
  }));

  const triggerLabel = getLanguageShortLabel(currentLanguage);

  if (options.length === 0) return null;

  const handleChange = (value: string) => {
    if (value === currentLanguage) return;
    setLanguage(value);
  };

  return (
    <EnhancedDropdown
      options={options}
      value={currentLanguage}
      onChange={handleChange}
      compactMode={true}
      customTriggerLabel={triggerLabel}
      dropdownTitle={t('common.languageSelector.title')}
      dropdownWidth="w-48"
      alignRight={true}
    />
  );
};

export default LanguageSelector;
