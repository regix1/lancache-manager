import { HTMLHeroUIProps } from '@heroui/system';
import { CellProps } from '@react-types/table';

type TableCellProps = CellProps & HTMLHeroUIProps<"td">;
declare const TableCell: (props: TableCellProps) => JSX.Element;

export { type TableCellProps, TableCell as default };
