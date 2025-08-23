import {
  useCode
} from "./chunk-UDFNFZDA.mjs";

// src/code.tsx
import { forwardRef } from "@heroui/system-rsc";
import { jsx } from "react/jsx-runtime";
var Code = forwardRef((props, ref) => {
  const { Component, children, getCodeProps } = useCode({ ...props });
  return /* @__PURE__ */ jsx(Component, { ref, ...getCodeProps(), children });
});
Code.displayName = "HeroUI.Code";
var code_default = Code;

export {
  code_default
};
