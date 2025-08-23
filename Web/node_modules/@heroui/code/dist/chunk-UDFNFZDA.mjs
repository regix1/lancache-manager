// src/use-code.ts
import { code } from "@heroui/theme";
import { mapPropsVariants } from "@heroui/system-rsc";
import { useMemo } from "react";
import { objectToDeps } from "@heroui/shared-utils";
function useCode(originalProps) {
  const [props, variantProps] = mapPropsVariants(originalProps, code.variantKeys);
  const { as, children, className, ...otherProps } = props;
  const Component = as || "code";
  const styles = useMemo(
    () => code({
      ...variantProps,
      className
    }),
    [objectToDeps(variantProps), className]
  );
  const getCodeProps = () => {
    return {
      className: styles,
      ...otherProps
    };
  };
  return { Component, children, getCodeProps };
}

export {
  useCode
};
