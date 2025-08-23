"use client";

// src/use-button-group.ts
import { buttonGroup } from "@heroui/theme";
import { mapPropsVariants, useProviderContext } from "@heroui/system";
import { useDOMRef } from "@heroui/react-utils";
import { useMemo, useCallback } from "react";
import { objectToDeps } from "@heroui/shared-utils";
function useButtonGroup(originalProps) {
  var _a, _b;
  const globalContext = useProviderContext();
  const [props, variantProps] = mapPropsVariants(originalProps, buttonGroup.variantKeys);
  const {
    ref,
    as,
    children,
    color = "default",
    size = "md",
    variant = "solid",
    radius,
    isDisabled = false,
    isIconOnly = false,
    disableRipple = (_a = globalContext == null ? void 0 : globalContext.disableRipple) != null ? _a : false,
    disableAnimation = (_b = globalContext == null ? void 0 : globalContext.disableAnimation) != null ? _b : false,
    className,
    ...otherProps
  } = props;
  const Component = as || "div";
  const domRef = useDOMRef(ref);
  const classNames = useMemo(
    () => buttonGroup({
      ...variantProps,
      className
    }),
    [objectToDeps(variantProps), className]
  );
  const context = useMemo(
    () => ({
      size,
      color,
      variant,
      radius,
      isIconOnly,
      isDisabled,
      disableAnimation,
      disableRipple,
      fullWidth: !!(originalProps == null ? void 0 : originalProps.fullWidth)
    }),
    [
      size,
      color,
      variant,
      radius,
      isDisabled,
      isIconOnly,
      disableAnimation,
      disableRipple,
      originalProps == null ? void 0 : originalProps.fullWidth
    ]
  );
  const getButtonGroupProps = useCallback(
    () => ({
      role: "group",
      ...otherProps
    }),
    [otherProps]
  );
  return {
    Component,
    children,
    domRef,
    context,
    classNames,
    getButtonGroupProps
  };
}

export {
  useButtonGroup
};
