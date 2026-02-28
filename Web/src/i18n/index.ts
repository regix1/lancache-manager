import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import en from './locales/en.json';

const STORAGE_KEY = 'lancache_language';
const storedLanguage =
  typeof window !== 'undefined' ? window.localStorage.getItem(STORAGE_KEY) : null;

i18n.use(initReactI18next).init({
  resources: {
    en: { translation: en }
  },
  lng: storedLanguage || 'en',
  fallbackLng: 'en',
  supportedLngs: ['en'],
  interpolation: {
    escapeValue: false
  },
  react: {
    useSuspense: false
  }
});

export const setLanguage = (language: string) => {
  i18n.changeLanguage(language);
  if (typeof window !== 'undefined') {
    window.localStorage.setItem(STORAGE_KEY, language);
  }
};

export default i18n;
