// src/use-kbd.ts
import { mapPropsVariants } from "@heroui/system-rsc";
import { kbd } from "@heroui/theme";
import { clsx, objectToDeps } from "@heroui/shared-utils";
import { useMemo } from "react";
function useKbd(originalProps) {
  const [props, variantProps] = mapPropsVariants(originalProps, kbd.variantKeys);
  const { as, children, className, keys, title, classNames, ...otherProps } = props;
  const Component = as || "kbd";
  const slots = useMemo(
    () => kbd({
      ...variantProps
    }),
    [objectToDeps(variantProps)]
  );
  const baseStyles = clsx(classNames == null ? void 0 : classNames.base, className);
  const keysToRender = typeof keys === "string" ? [keys] : Array.isArray(keys) ? keys : [];
  const getKbdProps = (props2 = {}) => ({
    ...otherProps,
    ...props2,
    className: clsx(slots.base({ class: clsx(baseStyles, props2.className) }))
  });
  return { Component, slots, classNames, title, children, keysToRender, getKbdProps };
}

export {
  useKbd
};
