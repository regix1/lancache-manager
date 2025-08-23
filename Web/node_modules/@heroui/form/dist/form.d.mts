import { FormProps } from './base-form.mjs';
import './utils.mjs';
import * as React from 'react';
import '@react-types/form';
import '@react-types/shared';

declare const Form: React.ForwardRefExoticComponent<FormProps & React.RefAttributes<HTMLFormElement>>;

export { Form };
