import { HTMLHeroUIProps } from '@heroui/system';
import { RowProps } from '@react-types/table';

type TableRowProps<T = object> = RowProps<T> & Omit<HTMLHeroUIProps<"tr">, keyof RowProps<T>>;
declare const TableRow: (props: TableRowProps) => JSX.Element;

export { type TableRowProps, TableRow as default };
