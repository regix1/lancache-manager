import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import reactPlugin from 'eslint-plugin-react';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import prettierConfig from 'eslint-config-prettier';
import prettierPlugin from 'eslint-plugin-prettier';

// A dialog opens from a click and closes only when the person closes it or its task succeeds.
// Shared by every block that declares no-restricted-syntax, since a block's list replaces the
// general one instead of merging with it.
const DIALOG_MOUNT_MESSAGE =
  'Do not open a dialog by mounting it. Render it every time with opened={...} and close it with opened={false}, so it plays its close animation instead of vanishing, and a reopen does not replay its entrance.';
// A dialog with no opened={...} of its own is open whenever it is mounted. A condition around a
// dialog that has one (a feature that is present or not) is fine.
const MOUNT_OPENED_DIALOG =
  "JSXElement[openingElement.name.name=/Modal$/]:not(:has(JSXAttribute[name.name='opened'][value.type='JSXExpressionContainer']))";
const DIALOG_RULES = [
  {
    selector:
      "JSXAttribute[name.name='opened'] Identifier[name=/[Ll]oading|[Ss]ubmitting|[Bb]usy|[Ss]aving/]",
    message:
      'Do not open or close a dialog from loading, submitting, busy or saving state: the dialog opens when a request starts and closes when it fails, which reads as the whole dialog fading out and back in. Open it from the click, and show progress and failures inside the open dialog.'
  },
  {
    selector: `LogicalExpression[operator='&&'] > ${MOUNT_OPENED_DIALOG}`,
    message: DIALOG_MOUNT_MESSAGE
  },
  // A ternary that picks one of two dialogs keeps a dialog mounted; one that picks a dialog or
  // nothing mounts it conditionally.
  {
    selector: `ConditionalExpression[alternate.type=/^(Literal|Identifier)$/] > ${MOUNT_OPENED_DIALOG}.consequent`,
    message: DIALOG_MOUNT_MESSAGE
  },
  {
    selector: `ConditionalExpression[consequent.type=/^(Literal|Identifier)$/] > ${MOUNT_OPENED_DIALOG}.alternate`,
    message: DIALOG_MOUNT_MESSAGE
  }
];

export default tseslint.config(
  // Ignore patterns
  {
    ignores: [
      'dist/**',
      'build/**',
      'node_modules/**',
      '*.config.js',
      '*.config.ts',
      'vite.config.ts',
      'tailwind.config.js',
      'postcss.config.js'
    ]
  },

  // Base JavaScript config
  js.configs.recommended,

  // TypeScript configs
  ...tseslint.configs.recommended,
  ...tseslint.configs.stylistic,

  // React config
  {
    files: ['**/*.{ts,tsx,js,jsx}'],
    plugins: {
      react: reactPlugin,
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh
    },
    languageOptions: {
      ecmaVersion: 2020,
      sourceType: 'module',
      globals: {
        ...globals.browser,
        ...globals.es2020
      },
      parser: tseslint.parser,
      parserOptions: {
        ecmaFeatures: {
          jsx: true
        }
      }
    },
    settings: {
      react: {
        version: 'detect'
      }
    },
    rules: {
      // React rules
      ...reactPlugin.configs.recommended.rules,
      ...reactHooks.configs.recommended.rules,
      'react/react-in-jsx-scope': 'off',
      'react/prop-types': 'off',
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],

      // TypeScript rules
      '@typescript-eslint/no-explicit-any': 'warn',
      '@typescript-eslint/no-unused-vars': [
        'warn',
        {
          argsIgnorePattern: '^_',
          varsIgnorePattern: '^_',
          caughtErrorsIgnorePattern: '^_'
        }
      ],
      '@typescript-eslint/consistent-type-imports': [
        'warn',
        {
          prefer: 'type-imports',
          fixStyle: 'inline-type-imports'
        }
      ],

      // General rules
      'no-console': ['warn', { allow: ['warn', 'error'] }],
      'prefer-const': 'warn',
      'no-unused-expressions': 'warn',
      'no-duplicate-imports': 'error',

      // Status string consistency: use "completed" not "complete"
      'no-restricted-syntax': [
        'error',
        {
          selector:
            "BinaryExpression[operator='==='][right.value='complete'][left.property.name='status']",
          message:
            "Use 'completed' instead of 'complete' for status checks. The backend sends 'completed' for all completion states."
        },
        {
          selector:
            "BinaryExpression[operator=/^[!=]==$/][right.value='connected'][left.property.name='connectionState']",
          message:
            'Do not compare connectionState to "connected". Read the isConnected boolean instead, and to re-fetch data once a dropped connection returns call useReconnectRefetch(isConnected, callback) from @hooks/useReconnectRefetch. A hand-rolled check re-fetches on every unrelated re-render and misses the first connect.'
        },
        {
          // The same comparison written against a destructured `connectionState`, which has no
          // `.property` for the selector above to match.
          selector:
            "BinaryExpression[operator=/^[!=]==$/][right.value='connected'][left.name='connectionState']",
          message:
            'Do not compare connectionState to "connected". Read the isConnected boolean instead, and to re-fetch data once a dropped connection returns call useReconnectRefetch(isConnected, callback) from @hooks/useReconnectRefetch. A hand-rolled check re-fetches on every unrelated re-render and misses the first connect.'
        },
        {
          selector:
            'VariableDeclarator[id.name=/^(wasDisconnected|prevConnectionState|everConnected|hasConnected|wasConnected)Ref$/]',
          message:
            'Do not hand-roll a reconnect latch. useReconnectRefetch(isConnected, callback) from @hooks/useReconnectRefetch already tracks the drop-and-return transition and is covered by scripts/test-reconnect-refetch.mjs.'
        },
        ...DIALOG_RULES
      ]
    }
  },

  // SignalR status string rules - scoped to notification/SignalR files
  {
    files: [
      'src/contexts/notifications/**/*.{ts,tsx}',
      'src/contexts/SignalRContext/**/*.{ts,tsx}'
    ],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          selector:
            "BinaryExpression[operator='==='][right.value='error'][left.property.name='status']",
          message:
            "Use 'failed' instead of 'error' for SignalR status checks. The backend sends 'failed' for all failure states."
        },
        {
          selector:
            "BinaryExpression[operator='==='][right.value='complete'][left.property.name='status']",
          message:
            "Use 'completed' instead of 'complete' for SignalR status checks. The backend sends 'completed' for all completion states."
        },
        // Repeated from the general block because a rule declared here replaces it rather than
        // merging with it, and these files are the likeliest place to hand-roll a reconnect.
        {
          selector:
            "BinaryExpression[operator=/^[!=]==$/][right.value='connected'][left.property.name='connectionState']",
          message:
            'Do not compare connectionState to "connected". Read the isConnected boolean instead, and to re-fetch data once a dropped connection returns call useReconnectRefetch(isConnected, callback) from @hooks/useReconnectRefetch. A hand-rolled check re-fetches on every unrelated re-render and misses the first connect.'
        },
        {
          // The same comparison written against a destructured `connectionState`, which has no
          // `.property` for the selector above to match.
          selector:
            "BinaryExpression[operator=/^[!=]==$/][right.value='connected'][left.name='connectionState']",
          message:
            'Do not compare connectionState to "connected". Read the isConnected boolean instead, and to re-fetch data once a dropped connection returns call useReconnectRefetch(isConnected, callback) from @hooks/useReconnectRefetch. A hand-rolled check re-fetches on every unrelated re-render and misses the first connect.'
        },
        {
          selector:
            'VariableDeclarator[id.name=/^(wasDisconnected|prevConnectionState|everConnected|hasConnected|wasConnected)Ref$/]',
          message:
            'Do not hand-roll a reconnect latch. useReconnectRefetch(isConnected, callback) from @hooks/useReconnectRefetch already tracks the drop-and-return transition and is covered by scripts/test-reconnect-refetch.mjs.'
        },
        ...DIALOG_RULES
      ]
    }
  },

  // Node scripts (git hooks installer, validators)
  {
    files: ['scripts/**/*.{js,mjs}'],
    languageOptions: {
      ecmaVersion: 2020,
      sourceType: 'module',
      globals: {
        ...globals.node
      }
    },
    rules: {
      'no-console': 'off'
    }
  },

  // Prettier config (must be last)
  prettierConfig,
  {
    plugins: {
      prettier: prettierPlugin
    },
    rules: {
      'prettier/prettier': 'warn'
    }
  }
);
