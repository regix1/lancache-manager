"use client";

// src/provider-context.ts
import { createContext } from "@heroui/react-utils";
var [ProviderContext, useProviderContext] = createContext({
  name: "ProviderContext",
  strict: false
});

export {
  ProviderContext,
  useProviderContext
};
