import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
// Relative, not the @utils alias: the .mjs test harness compiles this file directly and does not
// resolve path aliases, the way the locale imports below already account for.
import { storage } from '../utils/storage';
import en from './locales/en.json' with { type: 'json' };
import zh from './locales/zh.json' with { type: 'json' };

const STORAGE_KEY = 'lancache_language';
// Through the storage helper, not window.localStorage. This runs while the module graph is being
// evaluated, before anything can catch: a browser with site data blocked for the origin throws
// SecurityError on the property access itself, which would take the whole app down to a blank page
// before it renders. The helper probes once and falls back to memory, so the worst case is English.
const storedLanguage = storage.getItem(STORAGE_KEY);

i18n.use(initReactI18next).init({
  resources: {
    en: { translation: en },
    zh: { translation: zh }
  },
  lng: storedLanguage || 'en',
  fallbackLng: 'en',
  supportedLngs: ['en', 'zh'],
  interpolation: {
    escapeValue: false
  },
  react: {
    useSuspense: false
  }
});

export const setLanguage = (language: string) => {
  i18n.changeLanguage(language);
  storage.setItem(STORAGE_KEY, language);
};

export default i18n;
