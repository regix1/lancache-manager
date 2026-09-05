import { forwardRef, type ComponentPropsWithRef } from 'react';

interface TextInputProps extends Omit<ComponentPropsWithRef<'input'>, 'size' | 'type'> {
  size?: 'sm' | 'md';
}

export const TextInput = forwardRef<HTMLInputElement, TextInputProps>(function TextInput(
  { className = '', size = 'sm', ...inputProps },
  ref
) {
  const sizeClassName =
    size === 'sm'
      ? 'min-h-0 px-2 py-[0.1rem] text-sm leading-[1.3]'
      : 'control-h-md px-3 py-2 text-sm';

  return (
    <input
      {...inputProps}
      ref={ref}
      type="text"
      className={`themed-input w-full ${sizeClassName} ${className}`}
    />
  );
});
