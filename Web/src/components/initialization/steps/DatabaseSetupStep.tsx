import React, { useState, useCallback } from 'react';
import { Database, Eye, EyeOff, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { API_BASE } from '@utils/constants';

interface DatabaseSetupStepProps {
  onSetupComplete: () => void;
}

interface FormState {
  username: string;
  password: string;
  confirmPassword: string;
}

interface FormErrors {
  username: string | null;
  password: string | null;
  confirmPassword: string | null;
}

interface CredentialsResponse {
  success: boolean;
  message: string;
  error?: string;
}

export const DatabaseSetupStep: React.FC<DatabaseSetupStepProps> = ({ onSetupComplete }) => {
  const [form, setForm] = useState<FormState>({
    username: 'lancache',
    password: '',
    confirmPassword: ''
  });
  const [errors, setErrors] = useState<FormErrors>({
    username: null,
    password: null,
    confirmPassword: null
  });
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [setupSuccess, setSetupSuccess] = useState(false);

  const getPasswordStrength = useCallback((password: string): 'weak' | 'medium' | 'strong' => {
    const hasUppercase = /[A-Z]/.test(password);
    const hasLowercase = /[a-z]/.test(password);
    const hasNumbers = /[0-9]/.test(password);
    const hasSymbols = /[^A-Za-z0-9]/.test(password);
    const hasMixedCase = hasUppercase && hasLowercase;
    const hasNumbersOrSymbols = hasNumbers || hasSymbols;

    if (password.length >= 15 && hasMixedCase && hasNumbersOrSymbols) {
      return 'strong';
    }
    if (password.length >= 10 && (hasMixedCase || hasNumbers)) {
      return 'medium';
    }
    return 'weak';
  }, []);

  const validateForm = useCallback((): boolean => {
    const newErrors: FormErrors = {
      username: null,
      password: null,
      confirmPassword: null
    };

    if (!form.username.trim()) {
      newErrors.username = 'Username is required';
    }

    if (!form.password) {
      newErrors.password = 'Password is required';
    } else if (form.password.length < 8) {
      newErrors.password = 'Password must be at least 8 characters';
    } else {
      const blockedPasswords = [
        'lancache',
        'password',
        '12345678',
        'admin123',
        'qwerty123',
        'lancache1',
        'lancache123'
      ];
      if (blockedPasswords.includes(form.password.toLowerCase())) {
        newErrors.password = 'This password is too common. Please choose a more secure password.';
      }

      const username = form.username?.trim() || 'lancache';
      if (form.password.toLowerCase() === username.toLowerCase()) {
        newErrors.password = 'Password cannot be the same as the username';
      }
    }

    if (!form.confirmPassword) {
      newErrors.confirmPassword = 'Please confirm your password';
    } else if (form.password !== form.confirmPassword) {
      newErrors.confirmPassword = 'Passwords do not match';
    }

    setErrors(newErrors);
    return !newErrors.username && !newErrors.password && !newErrors.confirmPassword;
  }, [form]);

  const handleInputChange = useCallback(
    (field: keyof FormState) => (e: React.ChangeEvent<HTMLInputElement>) => {
      setForm((prev: FormState) => ({ ...prev, [field]: e.target.value }));
      setErrors((prev: FormErrors) => ({ ...prev, [field]: null }));
      setSubmitError(null);
    },
    []
  );

  const handleSubmit = useCallback(async () => {
    if (!validateForm()) {
      return;
    }

    setIsSubmitting(true);
    setSubmitError(null);

    try {
      const response = await fetch(`${API_BASE}/setup/credentials`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({
          username: form.username.trim(),
          password: form.password
        })
      });

      const data: CredentialsResponse = await response.json();

      if (response.ok && data.success) {
        setSetupSuccess(true);
        setTimeout(() => {
          onSetupComplete();
        }, 1500);
      } else {
        setSubmitError(
          data.error || data.message || 'Failed to save credentials. Please try again.'
        );
      }
    } catch (error: unknown) {
      const message =
        error instanceof Error ? error.message : 'Network error. Please check your connection.';
      setSubmitError(message);
    } finally {
      setIsSubmitting(false);
    }
  }, [form, validateForm, onSetupComplete]);

  if (setupSuccess) {
    return (
      <div className="space-y-5">
        {/* Header */}
        <div className="flex flex-col items-center text-center">
          <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-success">
            <CheckCircle className="w-7 h-7 icon-success" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">Setup Complete</h3>
          <p className="text-sm text-themed-secondary max-w-md">
            Database credentials saved successfully. Redirecting...
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
          <Database className="w-7 h-7 icon-info" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">Database Configuration</h3>
        <p className="text-sm text-themed-secondary max-w-md">
          Configure PostgreSQL credentials for your lancache-manager database
        </p>
      </div>

      {/* Username Input */}
      <div>
        <label className="block text-sm font-medium text-themed-secondary mb-1.5">Username</label>
        <input
          type="text"
          value={form.username}
          onChange={handleInputChange('username')}
          placeholder="Database username"
          className="w-full px-3 py-2.5 themed-input"
          autoComplete="username"
          disabled={isSubmitting}
        />
        {errors.username && <p className="text-xs text-themed-error mt-1">{errors.username}</p>}
      </div>

      {/* Password Input */}
      <div>
        <label className="block text-sm font-medium text-themed-secondary mb-1.5">Password</label>
        <div className="relative">
          <input
            type={showPassword ? 'text' : 'password'}
            value={form.password}
            onChange={handleInputChange('password')}
            placeholder="Enter a strong password"
            className="w-full px-3 py-2.5 pr-10 themed-input"
            autoComplete="new-password"
            disabled={isSubmitting}
          />
          <button
            type="button"
            className="absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded text-themed-muted"
            onClick={() => setShowPassword((prev: boolean) => !prev)}
            tabIndex={-1}
          >
            {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
          </button>
        </div>
        {errors.password && <p className="text-xs text-themed-error mt-1">{errors.password}</p>}
        {form.password && (
          <div className="flex items-center gap-2 mt-1">
            <div className="flex-1 h-1.5 rounded-full bg-themed-tertiary overflow-hidden">
              <div
                className={`h-full rounded-full transition-all ${
                  getPasswordStrength(form.password) === 'weak'
                    ? 'w-1/3 bg-red-500'
                    : getPasswordStrength(form.password) === 'medium'
                      ? 'w-2/3 bg-yellow-500'
                      : 'w-full bg-green-500'
                }`}
              />
            </div>
            <span
              className={`text-xs ${
                getPasswordStrength(form.password) === 'weak'
                  ? 'text-themed-error'
                  : getPasswordStrength(form.password) === 'medium'
                    ? 'text-themed-warning'
                    : 'text-themed-success'
              }`}
            >
              {getPasswordStrength(form.password) === 'weak'
                ? 'Weak'
                : getPasswordStrength(form.password) === 'medium'
                  ? 'Medium'
                  : 'Strong'}
            </span>
          </div>
        )}
      </div>

      {/* Confirm Password Input */}
      <div>
        <label className="block text-sm font-medium text-themed-secondary mb-1.5">
          Confirm Password
        </label>
        <div className="relative">
          <input
            type={showConfirmPassword ? 'text' : 'password'}
            value={form.confirmPassword}
            onChange={handleInputChange('confirmPassword')}
            placeholder="Confirm your password"
            className="w-full px-3 py-2.5 pr-10 themed-input"
            autoComplete="new-password"
            disabled={isSubmitting}
          />
          <button
            type="button"
            className="absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded text-themed-muted"
            onClick={() => setShowConfirmPassword((prev: boolean) => !prev)}
            tabIndex={-1}
          >
            {showConfirmPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
          </button>
        </div>
        {errors.confirmPassword && (
          <p className="text-xs text-themed-error mt-1">{errors.confirmPassword}</p>
        )}
      </div>

      {/* Submit Button */}
      <div className="space-y-3">
        <Button
          variant="filled"
          color="blue"
          onClick={handleSubmit}
          loading={isSubmitting}
          disabled={isSubmitting}
          fullWidth
        >
          {isSubmitting ? 'Saving credentials...' : 'Configure Database'}
        </Button>
      </div>

      {/* Info Box */}
      <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
        <p className="text-themed-secondary">
          <strong className="text-themed-primary">First-time setup:</strong> These credentials will
          be used to connect to the PostgreSQL database. The username is pre-filled with the default
          value. Choose a strong password with at least 8 characters.
        </p>
      </div>

      {/* Error */}
      {submitError && (
        <div className="p-3 rounded-lg bg-themed-error">
          <p className="text-sm text-themed-error">{submitError}</p>
        </div>
      )}
    </div>
  );
};
