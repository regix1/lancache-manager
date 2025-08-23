"use client";

// src/checkbox-group-context.ts
import { createContext } from "@heroui/react-utils";
var [CheckboxGroupProvider, useCheckboxGroupContext] = createContext({
  name: "CheckboxGroupContext",
  strict: false
});

export {
  CheckboxGroupProvider,
  useCheckboxGroupContext
};
