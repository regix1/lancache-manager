"use client";

// src/radio-group-context.ts
import { createContext } from "@heroui/react-utils";
var [RadioGroupProvider, useRadioGroupContext] = createContext({
  name: "RadioGroupContext",
  strict: false
});

export {
  RadioGroupProvider,
  useRadioGroupContext
};
