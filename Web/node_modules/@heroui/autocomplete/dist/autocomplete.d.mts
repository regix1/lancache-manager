import { ReactElement } from 'react';
import { UseAutocompleteProps } from './use-autocomplete.mjs';
import 'tailwind-variants';
import '@react-stately/combobox';
import '@heroui/system';
import '@heroui/theme';
import '@heroui/react-utils';
import '@react-types/combobox';
import '@heroui/popover';
import '@heroui/listbox';
import '@heroui/input';
import '@heroui/scroll-shadow';
import '@heroui/button';
import '@react-types/shared';

interface Props<T> extends UseAutocompleteProps<T> {
}
type AutocompleteProps<T extends object = object> = Props<T>;
declare const Autocomplete: <T extends object>(props: AutocompleteProps<T>) => ReactElement;

export { type AutocompleteProps, Autocomplete as default };
