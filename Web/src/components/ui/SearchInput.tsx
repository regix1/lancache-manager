import { noAutofill } from '@utils/autofill';
import { Search, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';

// Everything a plain text input accepts passes straight through, so a caller that needs an `id`
// for its own <label>, an `aria-label`, `disabled` or `autoFocus` just sets it - none of those
// need a bespoke prop here. `ref` is included so a caller driving its own keyboard flow can move
// focus back to the box. The native `size` attribute is dropped because it means a character
// count on an input, and this control's `size` picks a row height instead.
interface SearchInputProps extends Omit<React.ComponentPropsWithRef<'input'>, 'size'> {
  onClear?: () => void;
  size?: 'sm' | 'md';
}

export function SearchInput({ onClear, size = 'md', value, ...inputProps }: SearchInputProps) {
  const { t } = useTranslation();
  const isCompact = size === 'sm';

  return (
    <div className="relative">
      <Search className="input-icon absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
      <input
        type="text"
        {...inputProps}
        {...noAutofill}
        value={value}
        className={`themed-input w-full pl-10 ${
          isCompact ? 'input-search-sm pr-9' : 'control-h-md py-2 pr-11 text-sm'
        }`}
      />
      {onClear && value ? (
        <button
          type="button"
          onClick={onClear}
          aria-label={t('common.clearSearch')}
          className={`search-input-clear absolute top-1/2 -translate-y-1/2 flex items-center justify-center themed-border-radius-sm ${
            isCompact ? 'right-1 h-6 w-6' : 'right-1.5 h-8 w-8'
          }`}
        >
          <X className="w-4 h-4" />
        </button>
      ) : null}
    </div>
  );
}
