import { HTMLHeroUIProps } from '@heroui/system';
import { SpectrumColumnProps } from '@react-types/table';

type TableColumnProps<T> = Omit<SpectrumColumnProps<T>, "showDivider"> & Omit<HTMLHeroUIProps<"th">, keyof SpectrumColumnProps<T>>;
declare const TableColumn: <T>(props: TableColumnProps<T>) => JSX.Element;

export { type TableColumnProps, TableColumn as default };
