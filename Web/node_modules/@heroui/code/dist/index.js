"use strict";
var __defProp = Object.defineProperty;
var __getOwnPropDesc = Object.getOwnPropertyDescriptor;
var __getOwnPropNames = Object.getOwnPropertyNames;
var __hasOwnProp = Object.prototype.hasOwnProperty;
var __export = (target, all) => {
  for (var name in all)
    __defProp(target, name, { get: all[name], enumerable: true });
};
var __copyProps = (to, from, except, desc) => {
  if (from && typeof from === "object" || typeof from === "function") {
    for (let key of __getOwnPropNames(from))
      if (!__hasOwnProp.call(to, key) && key !== except)
        __defProp(to, key, { get: () => from[key], enumerable: !(desc = __getOwnPropDesc(from, key)) || desc.enumerable });
  }
  return to;
};
var __toCommonJS = (mod) => __copyProps(__defProp({}, "__esModule", { value: true }), mod);

// src/index.ts
var index_exports = {};
__export(index_exports, {
  Code: () => code_default,
  useCode: () => useCode
});
module.exports = __toCommonJS(index_exports);

// src/code.tsx
var import_system_rsc2 = require("@heroui/system-rsc");

// src/use-code.ts
var import_theme = require("@heroui/theme");
var import_system_rsc = require("@heroui/system-rsc");
var import_react = require("react");
var import_shared_utils = require("@heroui/shared-utils");
function useCode(originalProps) {
  const [props, variantProps] = (0, import_system_rsc.mapPropsVariants)(originalProps, import_theme.code.variantKeys);
  const { as, children, className, ...otherProps } = props;
  const Component = as || "code";
  const styles = (0, import_react.useMemo)(
    () => (0, import_theme.code)({
      ...variantProps,
      className
    }),
    [(0, import_shared_utils.objectToDeps)(variantProps), className]
  );
  const getCodeProps = () => {
    return {
      className: styles,
      ...otherProps
    };
  };
  return { Component, children, getCodeProps };
}

// src/code.tsx
var import_jsx_runtime = require("react/jsx-runtime");
var Code = (0, import_system_rsc2.forwardRef)((props, ref) => {
  const { Component, children, getCodeProps } = useCode({ ...props });
  return /* @__PURE__ */ (0, import_jsx_runtime.jsx)(Component, { ref, ...getCodeProps(), children });
});
Code.displayName = "HeroUI.Code";
var code_default = Code;
// Annotate the CommonJS export names for ESM import in node:
0 && (module.exports = {
  Code,
  useCode
});
