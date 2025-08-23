import { HTMLHeroUIProps } from '@heroui/system';
import { TableHeaderProps as TableHeaderProps$1 } from '@react-types/table';

type TableHeaderProps<T> = TableHeaderProps$1<T> & Omit<HTMLHeroUIProps<"thead">, keyof TableHeaderProps$1<T>>;
declare const TableHeader: <T>(props: TableHeaderProps<T>) => JSX.Element;

export { type TableHeaderProps, TableHeader as default };
