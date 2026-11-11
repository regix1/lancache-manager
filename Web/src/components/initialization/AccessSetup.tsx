import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, KeyRound, LogIn, ShieldCheck, Users } from 'lucide-react';
import { SetupGate } from '@components/modals/SetupGate';
import { StepHeader } from '@components/initialization/StepHeader';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Checkbox } from '@components/ui/Checkbox';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import FormField from '@components/ui/FormField';
import { PasswordField } from '@components/ui/PasswordField';
import { PasswordStrengthMeter } from '@components/ui/PasswordStrengthMeter';
import CredentialFields from '@components/ui/CredentialFields';
import { SelectableCard } from '@components/ui/SelectableCard';
import { LoginServiceMark } from '@components/features/auth/LoginServiceMark';
import type { EnvironmentField } from '@contexts/AuthContext.types';
import { useAuth } from '@contexts/useAuth';
import { useSetupStatus } from '@contexts/useSetupStatus';
import { useTimeoutCallback } from '@hooks/useTimeoutCallback';
import authService from '@services/auth.service';
import { usesOidc, type AccountMode } from '@utils/accountMode';
import { noAutofill } from '@utils/autofill';
import {
  validateAccountCredentials,
  type AccountCredentialErrors
} from '@utils/accountCredentials';
import {
  LOGIN_KINDS,
  callbackPaths,
  loginErrorKey,
  needsClientSecret,
  type LoginKind,
  type LoginService
} from '@utils/loginService';
import { copyText } from '@utils/clipboard';
import { getErrorMessage } from '@utils/error';
import { getApiUrl } from '@utils/constants';
import '@/styles/features/access-setup.css';

type AccessStep = 'choose' | 'configure' | 'test';
type CallbackKind = 'callback' | 'setupCallback';

// The five modes are alternatives, not a sequence, so they are grouped by what a person types at
// sign-in rather than numbered. Each group renders the way the platform step groups its cards.
const MODE_GROUPS: readonly { id: 'local' | 'sso' | 'open'; modes: readonly AccountMode[] }[] = [
  { id: 'local', modes: ['password', 'apiKeyPassword'] },
  { id: 'sso', modes: ['apiKeyOidc', 'oidc'] },
  { id: 'open', modes: ['unauthenticated'] }
];

const inputClassName = 'themed-input setup-input';
const panelClassName = 'rounded-lg border border-themed-secondary bg-themed-tertiary p-4 space-y-3';
const noCredentialErrors: AccountCredentialErrors = {
  username: null,
  password: null,
  confirmPassword: null
};

// A sign-in callback lands back here with non-secret markers: which test just succeeded, or which
// bounded failure category ended it. Both are read once before the mount effect strips them.
function readTestResult(): { testedId: string | null; errorCode: string | null } {
  const params = new URLSearchParams(window.location.search);
  return {
    testedId: params.get('loginTest') === 'success' ? params.get('loginId') : null,
    errorCode: params.get('oidcError')
  };
}

export function AccessSetup({ onClose }: { onClose?: () => void }) {
  const { t } = useTranslation();
  const {
    accountMode,
    authenticationSetupRequired,
    signInModeFromEnvironment,
    oidcPending,
    ownerOidcEnabled,
    ownerPasswordEnabled,
    loginServices,
    incompleteLoginServices,
    ownerLoginServices,
    loginSetupPending,
    pendingLoginKind,
    isMainAdmin,
    login,
    refreshAuth
  } = useAuth();
  const { setupStatus, refreshSetupStatus } = useSetupStatus();
  const pendingTest = loginSetupPending || oidcPending;
  const [mode, setMode] = useState<AccountMode>(accountMode);
  const [step, setStep] = useState<AccessStep>(pendingTest ? 'test' : 'choose');
  const [apiKey, setApiKey] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [ownerPassword, setOwnerPassword] = useState(false);
  // Which connection the stored owner reauthenticates through when more than one is offered.
  const [ownerService, setOwnerService] = useState<string>(ownerLoginServices[0] ?? '');
  const [resumeTest, setResumeTest] = useState(false);
  // A service set by environment variables is already connected, so the picker opens on it. One
  // the environment left incomplete opens next, so its missing settings are the first thing shown.
  const [kind, setKind] = useState<LoginKind>(
    () =>
      pendingLoginKind ??
      loginServices.find((service) => service.environmentFields.length > 0)?.kind ??
      incompleteLoginServices[0]?.kind ??
      'google'
  );
  // A connected kind keeps its stored credentials unless the owner asks to enter new ones.
  const [reconfigure, setReconfigure] = useState(false);
  const [authority, setAuthority] = useState('');
  const [clientId, setClientId] = useState('');
  const [clientSecret, setClientSecret] = useState('');
  const [tenant, setTenant] = useState('');
  const [teamId, setTeamId] = useState('');
  const [keyId, setKeyId] = useState('');
  const [privateKey, setPrivateKey] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [allowedSubjects, setAllowedSubjects] = useState('');
  const [advanced, setAdvanced] = useState(false);
  // Keep local testing details separate from the address requirements shown above the URLs.
  const [addressHelp, setAddressHelp] = useState(false);
  // A first local password for an owner created by external sign-in, asked for only when that
  // owner picks a password mode.
  const [localUsername, setLocalUsername] = useState('');
  const [localPassword, setLocalPassword] = useState('');
  const [localConfirm, setLocalConfirm] = useState('');
  const [localErrors, setLocalErrors] = useState<AccountCredentialErrors>(noCredentialErrors);
  const [copied, setCopied] = useState<CallbackKind | null>(null);
  const [acknowledged, setAcknowledged] = useState(false);
  const [busy, setBusy] = useState(false);
  const [testedId] = useState<string | null>(() => readTestResult().testedId);
  const [error, setError] = useState<string | null>(() => {
    const { errorCode } = readTestResult();
    return errorCode === null ? null : t(loginErrorKey(errorCode));
  });
  // Read when a save answers: the fields stay editable while it runs, so a change made meanwhile
  // keeps the page on the form instead of moving on as if that change were saved.
  const formValues = {
    mode,
    apiKey,
    acknowledged,
    kind,
    reconfigure,
    clientId,
    clientSecret,
    displayName,
    tenant,
    authority,
    teamId,
    keyId,
    privateKey,
    allowedSubjects,
    localUsername,
    localPassword,
    localConfirm
  };
  const formValuesRef = useRef(formValues);
  formValuesRef.current = formValues;
  const heading = useRef<HTMLHeadingElement>(null);
  const form = useRef<HTMLFormElement>(null);
  const errorMessage = useRef<HTMLDivElement>(null);
  const scheduleCopiedReset = useTimeoutCallback(2000);
  useEffect(() => {
    const url = new URL(window.location.href);
    for (const marker of ['oidcError', 'loginTest', 'loginId']) {
      url.searchParams.delete(marker);
    }
    window.history.replaceState(window.history.state, '', url);
    // The marker only says a test finished; whether the connection is active is read from the
    // server, never from the URL.
    if (readTestResult().testedId !== null) void refreshAuth();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => {
    if (error) {
      errorMessage.current?.focus();
      errorMessage.current?.scrollIntoView({ block: 'nearest' });
    }
  }, [error]);
  const needsSignIn = setupStatus?.accountExists === true && !isMainAdmin;
  const ownerSso =
    usesOidc(accountMode) ||
    (accountMode === 'unauthenticated' && ownerOidcEnabled && !ownerPassword);
  const oidc = usesOidc(mode);
  const https = window.location.protocol === 'https:';
  const modeBlocked = oidc && !https;
  const connected = (candidate: LoginKind): LoginService | undefined =>
    loginServices.find((service) => service.kind === candidate);
  const selectedConnected = connected(kind);
  // A save fills an incomplete environment login's environment-set fields from the environment
  // too, so they are shown read-only the same as a connected one's.
  const incompleteService = incompleteLoginServices.find((service) => service.kind === kind);
  const setByEnvironment = (field: EnvironmentField): boolean =>
    (selectedConnected ?? incompleteService)?.environmentFields.includes(field) === true;
  // New credentials are collected for a kind that has never been tested, or when the owner asks to
  // replace a tested connection's registration. Otherwise the tested connection is kept as it is.
  const credentialsNeeded = oidc && !needsSignIn && (!selectedConnected || reconfigure);
  const serviceName = (candidate: LoginKind) => t(`accessSetup.services.${candidate}.title`);
  // In running copy the custom kind is "your identity service", not the card title "Other".
  const serviceLabel = (candidate: LoginKind) =>
    candidate === 'customOidc' ? t('accessSetup.services.customOidc.name') : serviceName(candidate);
  const sentenceStartLabel = (candidate: LoginKind) =>
    candidate === 'customOidc'
      ? t('accessSetup.services.customOidc.nameAtStart')
      : serviceName(candidate);
  const testedService = testedId
    ? loginServices.find((service) => service.id === testedId)
    : undefined;
  const testConfirmed = testedService !== undefined && !pendingTest;
  const pendingName = pendingLoginKind ? serviceLabel(pendingLoginKind) : t('accessSetup.sso');
  // An owner whose account was created by external sign-in has no local password, so a password
  // mode cannot be activated until one exists.
  const needsLocalPassword =
    !needsSignIn &&
    setupStatus?.accountExists === true &&
    !ownerPasswordEnabled &&
    (mode === 'password' || mode === 'apiKeyPassword');
  const paths = callbackPaths(kind);
  const callback = new URL(`${getApiUrl()}${paths.callback}`, window.location.origin).href;
  const setupCallback = new URL(`${getApiUrl()}${paths.setupCallback}`, window.location.origin)
    .href;
  // The flow only grows to three steps once new credentials are committed with Continue, so
  // comparing the choices never resizes the progress bar.
  const stepNumber = step === 'choose' ? 1 : step === 'configure' ? 2 : 3;
  const showsTestStep =
    step === 'test' ||
    (step === 'configure' && credentialsNeeded) ||
    (step === 'choose' && pendingTest);
  const totalSteps = showsTestStep ? 3 : 2;

  const changeStep = (next: AccessStep) => {
    // Moving focus before unmounting emits blur so autofill menus can dismiss their old anchor.
    heading.current?.focus();
    setError(null);
    if (next === 'choose') setResumeTest(false);
    setStep(next);
    requestAnimationFrame(() => heading.current?.focus());
  };

  const copyCallback = async (target: CallbackKind, value: string) => {
    if (await copyText(value)) {
      setCopied(target);
      scheduleCopiedReset(() => setCopied(null));
      return;
    }
    setError(t('accessSetup.copyFailed'));
  };

  // Opens the identity service for the pending connection. The page leaves on success; on failure
  // the test step stays with the key still filled in so the attempt can be repeated.
  const startTest = async (loginId: string | null) => {
    const result = loginId
      ? await authService.startLogin(loginId, apiKey.trim(), true)
      : await authService.startOidc(apiKey.trim(), true);
    window.location.assign(result.url);
  };

  const save = async () => {
    if (busy || modeBlocked) return;
    if (!apiKey.trim()) {
      setError(t('accessSetup.keyRequired'));
      return;
    }
    if (needsLocalPassword) {
      const errors = validateAccountCredentials(localUsername, localPassword, localConfirm);
      setLocalErrors(errors);
      if (Object.values(errors).some((failure) => failure !== null)) return;
    }
    setBusy(true);
    setError(null);
    let sent = formValues;
    const editedSince = (): boolean =>
      JSON.stringify(formValuesRef.current) !== JSON.stringify(sent);
    try {
      if (needsSignIn) {
        if (ownerSso) {
          const result = ownerService
            ? await authService.startLogin(ownerService, apiKey.trim(), false, true)
            : await authService.startOidc(apiKey.trim(), false, true);
          window.location.assign(result.url);
          return;
        }
        const result = await login(apiKey.trim(), username.trim(), password);
        if (!result.success) {
          setError(result.message);
          return;
        }
        // A password retyped while the sign-in ran stays in the box.
        setPassword((current) => (current === password ? '' : current));
        if (resumeTest && pendingTest) changeStep('test');
        else requestAnimationFrame(() => heading.current?.focus());
        return;
      }
      if (needsLocalPassword) {
        await authService.setMainAdminPassword(apiKey.trim(), localUsername.trim(), localPassword);
        if (editedSince()) {
          await refreshAuth();
          setError(t('accessSetup.changedWhileSaving'));
          return;
        }
        setLocalPassword('');
        setLocalConfirm('');
        sent = { ...sent, localPassword: '', localConfirm: '' };
      }
      // With the mode and the connection both set by environment variables there is nothing to
      // save: the owner signs in through that connection, and that sign-in completes setup.
      if (
        authenticationSetupRequired &&
        signInModeFromEnvironment &&
        oidc &&
        !credentialsNeeded &&
        selectedConnected !== undefined &&
        selectedConnected.environmentFields.length > 0
      ) {
        changeStep('test');
        await startTest(selectedConnected.id);
        return;
      }
      const subjects = allowedSubjects
        .split('\n')
        .map((subject) => subject.trim())
        .filter(Boolean);
      const result = await authService.configureAccess({
        mode,
        apiKey: apiKey.trim(),
        acknowledgeUnauthenticated: acknowledged,
        ...(credentialsNeeded
          ? {
              // Environment-set fields stay out of the body; the server fills them itself.
              login: {
                kind,
                ...(setByEnvironment('clientId') ? {} : { clientId: clientId.trim() }),
                ...(needsClientSecret(kind) && !setByEnvironment('clientSecret')
                  ? { clientSecret }
                  : {}),
                ...(displayName.trim() && !setByEnvironment('displayName')
                  ? { displayName: displayName.trim() }
                  : {}),
                ...(kind === 'microsoft' && !setByEnvironment('tenant')
                  ? { tenant: tenant.trim() }
                  : {}),
                ...(kind === 'customOidc' && !setByEnvironment('authority')
                  ? { authority: authority.trim() }
                  : {}),
                ...(kind === 'apple'
                  ? {
                      ...(setByEnvironment('teamId') ? {} : { teamId: teamId.trim() }),
                      ...(setByEnvironment('keyId') ? {} : { keyId: keyId.trim() }),
                      ...(setByEnvironment('privateKey') ? {} : { privateKey })
                    }
                  : {}),
                ...(subjects.length && !setByEnvironment('allowedSubjects')
                  ? { allowedSubjects: subjects }
                  : {})
              }
            }
          : {})
      });
      if (editedSince()) {
        await refreshAuth();
        setError(t('accessSetup.changedWhileSaving'));
        return;
      }
      setPassword('');
      setClientSecret('');
      setPrivateKey('');
      if (credentialsNeeded || result.requiresLoginTest || result.requiresOidcTest) {
        await refreshAuth();
        changeStep('test');
        await startTest(result.pendingLoginId ?? (credentialsNeeded ? kind : pendingLoginKind));
        return;
      }
      setApiKey('');
      await refreshAuth();
      await refreshSetupStatus();
      onClose?.();
    } catch (failure: unknown) {
      setError(getErrorMessage(failure));
    } finally {
      setBusy(false);
    }
  };

  const testSignIn = async () => {
    if (busy || !https) return;
    if (needsSignIn) {
      setResumeTest(true);
      changeStep('configure');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await startTest(pendingLoginKind);
    } catch (failure: unknown) {
      setError(getErrorMessage(failure));
      setBusy(false);
    }
  };

  const renderCallback = (target: CallbackKind, value: string) => (
    <div>
      <FormField label={t(`accessSetup.${target}`)}>
        {(field) => (
          <div className="access-setup-copy-row">
            <input
              {...field}
              {...noAutofill}
              type="url"
              readOnly
              value={value}
              className={`${inputClassName} access-setup-callback`}
              onFocus={(event) => event.target.select()}
            />
            <Button
              type="button"
              variant="default"
              className="access-setup-copy"
              onClick={() => void copyCallback(target, value)}
            >
              {t(copied === target ? 'accessSetup.copied' : 'accessSetup.copy')}
            </Button>
          </div>
        )}
      </FormField>
    </div>
  );

  const renderTextField = (
    label: string,
    value: string,
    onChange: (next: string) => void,
    extra: {
      hint?: string;
      placeholder?: string;
      type?: string;
      environmentField?: EnvironmentField;
    } = {}
  ) => {
    // The server fills an environment-set field itself, so it is shown read-only and empty.
    const fromEnvironment =
      extra.environmentField !== undefined && setByEnvironment(extra.environmentField);
    return (
      <FormField
        label={label}
        required={!fromEnvironment}
        hint={fromEnvironment ? t('accessSetup.environment.setByEnvironment') : extra.hint}
      >
        {(field) => (
          <input
            {...field}
            {...noAutofill}
            type={extra.type ?? 'text'}
            value={fromEnvironment ? '' : value}
            required={!fromEnvironment}
            disabled={fromEnvironment}
            placeholder={fromEnvironment ? undefined : extra.placeholder}
            className={inputClassName}
            onChange={(event) => onChange(event.target.value)}
          />
        )}
      </FormField>
    );
  };

  // A short-label button that opens a region of secondary guidance below itself.
  const renderDisclosure = (
    label: string,
    open: boolean,
    toggle: () => void,
    children: ReactNode
  ) => (
    <div>
      <Button
        type="button"
        variant="default"
        size="sm"
        aria-expanded={open}
        onClick={toggle}
        rightSection={
          <ChevronDown className={`w-4 h-4 transition ${open ? 'rotate-180' : 'rotate-0'}`} />
        }
      >
        {label}
      </Button>
      <CollapsibleRegion open={open} contentClassName="space-y-4 pt-4">
        {children}
      </CollapsibleRegion>
    </div>
  );

  const ownerKeyField = (
    <PasswordField
      label={t('modals.auth.labels.apiKey')}
      value={apiKey}
      onChange={(event) => setApiKey(event.target.value)}
      inputClassName={inputClassName}
      autoComplete="off"
      name="installation-key"
      showPasswordLabel={t('accessSetup.showSecret')}
      hidePasswordLabel={t('accessSetup.hideSecret')}
    />
  );

  return (
    <SetupGate
      maxWidth="4xl"
      onClose={!authenticationSetupRequired && !busy ? onClose : undefined}
      footer={
        <div className="setup-actions setup-actions--split">
          {step !== 'choose' ? (
            <Button
              type="button"
              variant="default"
              onClick={(event) => {
                event.preventDefault();
                changeStep('choose');
              }}
              disabled={busy}
            >
              {t('accessSetup.back')}
            </Button>
          ) : onClose && !authenticationSetupRequired ? (
            <Button type="button" variant="default" onClick={onClose}>
              {t(testConfirmed ? 'accessSetup.done' : 'accessSetup.cancel')}
            </Button>
          ) : (
            <span />
          )}
          {step === 'choose' ? (
            <Button
              key="choose"
              type="button"
              color="primary"
              variant="filled"
              disabled={busy || modeBlocked}
              onClick={(event) => {
                event.preventDefault();
                if (modeBlocked) return;
                changeStep('configure');
              }}
            >
              {t('accessSetup.continue')}
            </Button>
          ) : step === 'configure' ? (
            credentialsNeeded ? (
              <span />
            ) : (
              <Button
                key="configure"
                type="submit"
                form="access-setup-form"
                color="primary"
                variant="filled"
                loading={busy}
                stableWidth
                disabled={
                  busy ||
                  modeBlocked ||
                  !apiKey.trim() ||
                  (!needsSignIn && mode === 'unauthenticated' && !acknowledged)
                }
              >
                {t(needsSignIn ? 'accessSetup.ownerSignInButton' : 'accessSetup.save')}
              </Button>
            )
          ) : (
            <Button
              key="test"
              type="button"
              color="primary"
              variant="filled"
              loading={busy}
              stableWidth
              disabled={busy || !https || !apiKey.trim()}
              onClick={(event) => {
                event.preventDefault();
                void testSignIn();
              }}
            >
              {t('accessSetup.testConnection')}
            </Button>
          )}
        </div>
      }
      icon={<ShieldCheck className="w-5 h-5 text-primary" aria-hidden="true" />}
      title={t('accessSetup.header')}
      steps={{ current: stepNumber, total: totalSteps, label: t('accessSetup.progress') }}
    >
      <div className="space-y-5 access-setup">
        {step === 'choose' && (
          <>
            <StepHeader
              headingRef={heading}
              icon={<Users className="w-7 h-7 icon-primary" />}
              iconBackground="bg-themed-primary-subtle"
              title={t('accessSetup.headings.choose')}
              description={t('accessSetup.descriptions.choose')}
            />
            {testedId !== null &&
              (testConfirmed ? (
                <div role="status">
                  <Alert color="success">
                    {t('accessSetup.testSucceeded', { name: testedService.displayName })}
                  </Alert>
                </div>
              ) : (
                <Alert color="info">{t('accessSetup.testUnconfirmed')}</Alert>
              ))}
            {authenticationSetupRequired && setupStatus?.isCompleted && (
              <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
                <p className="text-themed-secondary">{t('accessSetup.upgrade')}</p>
              </div>
            )}
            {signInModeFromEnvironment && (
              <p className="text-sm text-themed-secondary">
                {t(
                  modeBlocked
                    ? 'accessSetup.environment.modeBlocked'
                    : 'accessSetup.environment.modeSetByEnvironment'
                )}
              </p>
            )}
            <fieldset className="space-y-5 access-setup-choices">
              <legend className="sr-only">{t('accessSetup.headings.choose')}</legend>
              {MODE_GROUPS.map((group) => (
                <div key={group.id} className="space-y-2">
                  <p className="text-sm font-medium text-themed-secondary">
                    {t(`accessSetup.groups.${group.id}`)}
                  </p>
                  {group.id === 'sso' && loginServices.length > 0 && (
                    <p className="text-xs text-themed-muted">
                      {t('accessSetup.connectedList', {
                        names: loginServices.map((service) => service.displayName).join(', ')
                      })}
                    </p>
                  )}
                  <div
                    className={group.modes.length > 1 ? 'grid gap-3 sm:grid-cols-2' : 'grid gap-3'}
                  >
                    {group.modes.map((choice) => (
                      <SelectableCard
                        key={choice}
                        name="account-mode"
                        value={choice}
                        checked={mode === choice}
                        disabled={
                          (usesOidc(choice) && !https) ||
                          (signInModeFromEnvironment && choice !== accountMode)
                        }
                        disabledReason={
                          signInModeFromEnvironment && choice !== accountMode
                            ? t('accessSetup.environment.modeSetByEnvironment')
                            : usesOidc(choice) && !https
                              ? t('accessSetup.ssoRequiresHttps')
                              : undefined
                        }
                        onChange={() => {
                          setMode(choice);
                          setAcknowledged(false);
                        }}
                        title={t(`accessSetup.modes.${choice}.title`)}
                        description={t(`accessSetup.modes.${choice}.description`)}
                        note={t(`accessSetup.modes.${choice}.warning`)}
                        badge={
                          usesOidc(choice) && !https ? (
                            <Badge variant="neutral">{t('accessSetup.httpsRequired')}</Badge>
                          ) : undefined
                        }
                      />
                    ))}
                  </div>
                </div>
              ))}
            </fieldset>
            <p className="text-xs text-themed-muted">{t('accessSetup.keyOwnership')}</p>
          </>
        )}

        {step === 'configure' && (
          <form
            ref={form}
            id="access-setup-form"
            method="post"
            className="space-y-5"
            onSubmit={(event) => {
              event.preventDefault();
              void save();
            }}
          >
            <StepHeader
              headingRef={heading}
              icon={<KeyRound className="w-7 h-7 icon-info" />}
              iconBackground="bg-themed-info"
              title={t(`accessSetup.modes.${mode}.title`)}
              description={t('accessSetup.descriptions.configure')}
            />
            {modeBlocked && <Alert color="warning">{t('accessSetup.ssoRequiresHttps')}</Alert>}
            {setupStatus?.isCompleted && (
              <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
                <p className="text-themed-secondary">{t('accessSetup.sessions')}</p>
              </div>
            )}
            {!oidc && !window.isSecureContext && (
              <Alert color="warning">{t('accessSetup.httpsWarning')}</Alert>
            )}
            <section className={panelClassName} aria-labelledby="access-owner-title">
              <h4 id="access-owner-title" className="font-semibold text-themed-primary">
                {t('accessSetup.ownerHeading')}
              </h4>
              <p className="text-sm text-themed-secondary">{t('accessSetup.ownerDescription')}</p>
              <div>
                {ownerKeyField}
                <p className="text-xs text-themed-muted mt-1">
                  {t('accessSetup.keyLocation')} <code>/data/security/api_key.txt</code>
                </p>
              </div>
              {needsSignIn && (
                <>
                  <Alert color="info">{t('accessSetup.ownerSignIn')}</Alert>
                  {!ownerSso && (
                    <CredentialFields
                      apiKey={apiKey}
                      username={username}
                      password={password}
                      accountMode="password"
                      apiKeyPlaceholder=""
                      onChange={(field, value) =>
                        field === 'username' ? setUsername(value) : setPassword(value)
                      }
                      onSubmit={() => form.current?.requestSubmit()}
                    />
                  )}
                  {ownerSso && ownerLoginServices.length > 1 && (
                    <fieldset className="space-y-2">
                      <legend className="text-sm font-medium text-themed-secondary">
                        {t('accessSetup.ownerServiceHeading')}
                      </legend>
                      <div className="grid gap-3 sm:grid-cols-2">
                        {ownerLoginServices.map((id) => {
                          const service = loginServices.find((entry) => entry.id === id);
                          return (
                            <SelectableCard
                              key={id}
                              name="owner-service"
                              value={id}
                              checked={ownerService === id}
                              onChange={() => setOwnerService(id)}
                              icon={service && <LoginServiceMark kind={service.kind} />}
                              title={service?.displayName ?? id}
                            />
                          );
                        })}
                      </div>
                    </fieldset>
                  )}
                  {accountMode === 'unauthenticated' && ownerOidcEnabled && (
                    <Button
                      type="button"
                      variant="default"
                      className="access-setup-wrap"
                      disabled={busy}
                      onClick={() => setOwnerPassword(!ownerPassword)}
                    >
                      {t(ownerPassword ? 'accessSetup.ownerSso' : 'accessSetup.ownerPassword')}
                    </Button>
                  )}
                </>
              )}
            </section>
            {needsLocalPassword && (
              <section className={panelClassName} aria-labelledby="access-local-title">
                <h4 id="access-local-title" className="font-semibold text-themed-primary">
                  {t('accessSetup.localPasswordHeading')}
                </h4>
                <p className="text-sm text-themed-secondary">
                  {t('accessSetup.localPasswordDescription')}
                </p>
                <div>
                  <FormField
                    label={t('initialization.adminAccount.usernameLabel')}
                    required
                    error={localErrors.username && t(localErrors.username)}
                  >
                    {(field) => (
                      <input
                        {...field}
                        value={localUsername}
                        required
                        autoComplete="username"
                        name="username"
                        className={inputClassName}
                        onChange={(event) => {
                          setLocalUsername(event.target.value);
                          setLocalErrors((previous) => ({ ...previous, username: null }));
                        }}
                      />
                    )}
                  </FormField>
                </div>
                <div className="grid gap-4 sm:grid-cols-2">
                  <div>
                    <PasswordField
                      label={t('initialization.adminAccount.passwordLabel')}
                      value={localPassword}
                      onChange={(event) => {
                        setLocalPassword(event.target.value);
                        setLocalErrors((previous) => ({ ...previous, password: null }));
                      }}
                      error={localErrors.password && t(localErrors.password)}
                      autoComplete="new-password"
                      name="password"
                      inputClassName={inputClassName}
                      showPasswordLabel={t('aria.showPassword')}
                      hidePasswordLabel={t('aria.hidePassword')}
                    />
                    <PasswordStrengthMeter
                      password={localPassword}
                      weakLabel={t('passwordStrength.weak')}
                      mediumLabel={t('passwordStrength.medium')}
                      strongLabel={t('passwordStrength.strong')}
                    />
                  </div>
                  <div>
                    <PasswordField
                      label={t('initialization.adminAccount.confirmPasswordLabel')}
                      value={localConfirm}
                      onChange={(event) => {
                        setLocalConfirm(event.target.value);
                        setLocalErrors((previous) => ({ ...previous, confirmPassword: null }));
                      }}
                      error={localErrors.confirmPassword && t(localErrors.confirmPassword)}
                      autoComplete="new-password"
                      name="confirm-password"
                      inputClassName={inputClassName}
                      showPasswordLabel={t('aria.showPassword')}
                      hidePasswordLabel={t('aria.hidePassword')}
                    />
                  </div>
                </div>
              </section>
            )}
            {oidc && !needsSignIn && (
              <>
                <fieldset className="space-y-2">
                  <legend className="text-sm font-medium text-themed-secondary">
                    {t('accessSetup.serviceHeading')}
                  </legend>
                  <p className="text-xs text-themed-muted">{t('accessSetup.serviceHint')}</p>
                  <div className="grid gap-3 auto-rows-fr sm:grid-cols-2 lg:grid-cols-3">
                    {LOGIN_KINDS.map((candidate) => (
                      <SelectableCard
                        key={candidate}
                        layout="stack"
                        name="login-kind"
                        value={candidate}
                        checked={kind === candidate}
                        onChange={() => {
                          setKind(candidate);
                          setReconfigure(false);
                          setClientId('');
                          setClientSecret('');
                          setPrivateKey('');
                          setTeamId('');
                          setKeyId('');
                          setTenant('');
                          setAuthority('');
                          setDisplayName('');
                          setAllowedSubjects('');
                        }}
                        icon={<LoginServiceMark kind={candidate} />}
                        title={serviceName(candidate)}
                        note={t(`accessSetup.services.${candidate}.note`)}
                        badge={
                          connected(candidate) ? (
                            <Badge variant="success">{t('accessSetup.connected')}</Badge>
                          ) : undefined
                        }
                      />
                    ))}
                  </div>
                </fieldset>
                {!credentialsNeeded && (
                  <section className={panelClassName} aria-labelledby="access-connected-title">
                    <h4 id="access-connected-title" className="font-semibold text-themed-primary">
                      {t('accessSetup.connectedHeading', { name: sentenceStartLabel(kind) })}
                    </h4>
                    <p className="text-sm text-themed-secondary">
                      {t('accessSetup.connectedDescription', { name: serviceLabel(kind) })}
                    </p>
                    <Button
                      type="button"
                      variant="default"
                      size="sm"
                      disabled={busy}
                      onClick={() => setReconfigure(true)}
                    >
                      {t('accessSetup.reconfigure')}
                    </Button>
                  </section>
                )}
                {credentialsNeeded && (
                  <>
                    <section className={panelClassName} aria-labelledby="access-register-title">
                      <h4 id="access-register-title" className="font-semibold text-themed-primary">
                        {t('accessSetup.registerHeading', { name: serviceLabel(kind) })}
                      </h4>
                      <p className="text-sm text-themed-secondary">
                        {t(`accessSetup.services.${kind}.register`)}
                      </p>
                      <Alert color="info">
                        <p>{t(`accessSetup.services.${kind}.addresses`)}</p>
                        <p>{t('accessSetup.privateHosting')}</p>
                      </Alert>
                      {renderCallback('callback', callback)}
                      {renderCallback('setupCallback', setupCallback)}
                      {renderDisclosure(
                        t('accessSetup.addressHelp'),
                        addressHelp,
                        () => setAddressHelp((open) => !open),
                        <div className="space-y-2 text-xs text-themed-muted">
                          <p>{t(`accessSetup.services.${kind}.testing`)}</p>
                          {kind !== 'apple' && <p>{t('accessSetup.localAddress')}</p>}
                          <p>{t('accessSetup.callbackOrigin')}</p>
                        </div>
                      )}
                    </section>
                    <section
                      key={kind}
                      className={panelClassName}
                      aria-labelledby="access-oidc-title"
                    >
                      <h4 id="access-oidc-title" className="font-semibold text-themed-primary">
                        {t('accessSetup.credentialsHeading', { name: sentenceStartLabel(kind) })}
                      </h4>
                      <p className="text-sm text-themed-secondary">
                        {t(`accessSetup.services.${kind}.credentials`)}
                      </p>
                      {selectedConnected === undefined && incompleteService !== undefined && (
                        <Alert color="warning">{t('accessSetup.environment.incomplete')}</Alert>
                      )}
                      <div className="grid gap-4 sm:grid-cols-2">
                        {kind === 'customOidc' && (
                          <div className="sm:col-span-2">
                            {renderTextField(t('accessSetup.issuer'), authority, setAuthority, {
                              hint: t('accessSetup.issuerHint'),
                              placeholder: 'https://auth.example.com',
                              type: 'url',
                              environmentField: 'authority'
                            })}
                          </div>
                        )}
                        {kind === 'microsoft' && (
                          <div className="sm:col-span-2">
                            {renderTextField(t('accessSetup.tenant'), tenant, setTenant, {
                              hint: t('accessSetup.tenantHint'),
                              placeholder: '00000000-0000-0000-0000-000000000000',
                              environmentField: 'tenant'
                            })}
                          </div>
                        )}
                        <div>
                          {renderTextField(
                            t(kind === 'apple' ? 'accessSetup.servicesId' : 'accessSetup.clientId'),
                            clientId,
                            setClientId,
                            { environmentField: 'clientId' }
                          )}
                        </div>
                        {needsClientSecret(kind) && setByEnvironment('clientSecret') ? (
                          <div>
                            {renderTextField(t('accessSetup.clientSecret'), '', setClientSecret, {
                              type: 'password',
                              environmentField: 'clientSecret'
                            })}
                          </div>
                        ) : needsClientSecret(kind) ? (
                          <div>
                            <PasswordField
                              label={t('accessSetup.clientSecret')}
                              value={clientSecret}
                              onChange={(event) => setClientSecret(event.target.value)}
                              autoComplete="off"
                              name="oauth-client-secret"
                              inputClassName={inputClassName}
                              showPasswordLabel={t('accessSetup.showSecret')}
                              hidePasswordLabel={t('accessSetup.hideSecret')}
                            />
                          </div>
                        ) : (
                          <>
                            <div>
                              {renderTextField(t('accessSetup.teamId'), teamId, setTeamId, {
                                environmentField: 'teamId'
                              })}
                            </div>
                            <div>
                              {renderTextField(t('accessSetup.keyId'), keyId, setKeyId, {
                                environmentField: 'keyId'
                              })}
                            </div>
                            <div className="sm:col-span-2">
                              <FormField
                                label={t('accessSetup.privateKey')}
                                required={!setByEnvironment('privateKey')}
                                hint={t(
                                  setByEnvironment('privateKey')
                                    ? 'accessSetup.environment.setByEnvironment'
                                    : 'accessSetup.privateKeyHint'
                                )}
                              >
                                {(field) => (
                                  <textarea
                                    {...field}
                                    {...noAutofill}
                                    name="oauth-private-key"
                                    value={setByEnvironment('privateKey') ? '' : privateKey}
                                    rows={4}
                                    required={!setByEnvironment('privateKey')}
                                    disabled={setByEnvironment('privateKey')}
                                    spellCheck={false}
                                    className={inputClassName}
                                    onChange={(event) => setPrivateKey(event.target.value)}
                                  />
                                )}
                              </FormField>
                            </div>
                          </>
                        )}
                      </div>
                      {renderDisclosure(
                        t('accessSetup.advanced'),
                        advanced,
                        () => setAdvanced((open) => !open),
                        <>
                          <p className="text-xs text-themed-muted">
                            {t('accessSetup.advancedHint')}
                          </p>
                          <div>
                            <FormField
                              label={t('accessSetup.displayName')}
                              hint={
                                setByEnvironment('displayName')
                                  ? t('accessSetup.environment.setByEnvironment')
                                  : undefined
                              }
                            >
                              {(field) => (
                                <input
                                  {...field}
                                  {...noAutofill}
                                  name="connection-name"
                                  value={setByEnvironment('displayName') ? '' : displayName}
                                  disabled={setByEnvironment('displayName')}
                                  maxLength={80}
                                  className={inputClassName}
                                  onChange={(event) => setDisplayName(event.target.value)}
                                />
                              )}
                            </FormField>
                          </div>
                          <div>
                            <FormField
                              label={t('accessSetup.allowedSubjects')}
                              hint={t(
                                setByEnvironment('allowedSubjects')
                                  ? 'accessSetup.environment.setByEnvironment'
                                  : `accessSetup.services.${kind}.subjects`
                              )}
                            >
                              {(field) => (
                                <textarea
                                  {...field}
                                  {...noAutofill}
                                  name="allowed-subjects"
                                  value={setByEnvironment('allowedSubjects') ? '' : allowedSubjects}
                                  rows={3}
                                  disabled={setByEnvironment('allowedSubjects')}
                                  className={inputClassName}
                                  onChange={(event) => setAllowedSubjects(event.target.value)}
                                />
                              )}
                            </FormField>
                          </div>
                        </>
                      )}
                    </section>
                    <section className={panelClassName} aria-labelledby="access-test-title">
                      <h4 id="access-test-title" className="font-semibold text-themed-primary">
                        {t('accessSetup.testHeading')}
                      </h4>
                      <p className="text-sm text-themed-secondary">
                        {t('accessSetup.testConnectionHint', { name: serviceLabel(kind) })}
                      </p>
                      <Button
                        type="submit"
                        form="access-setup-form"
                        color="primary"
                        variant="filled"
                        loading={busy}
                        stableWidth
                        disabled={busy || modeBlocked || !apiKey.trim()}
                      >
                        {t('accessSetup.testConnection')}
                      </Button>
                    </section>
                  </>
                )}
              </>
            )}
            {mode === 'unauthenticated' && !needsSignIn && (
              <Alert color="warning">
                <Checkbox
                  checked={acknowledged}
                  onChange={(event) => setAcknowledged(event.target.checked)}
                  className="flex-shrink-0"
                  label={t('accessSetup.acknowledge')}
                />
              </Alert>
            )}
          </form>
        )}

        {step === 'test' && (
          <>
            <StepHeader
              headingRef={heading}
              icon={<LogIn className="w-7 h-7 icon-success" />}
              iconBackground="bg-themed-success"
              title={t('accessSetup.headings.test', { name: pendingName })}
              description={t('accessSetup.testExplanation', { name: pendingName })}
            />
            {!https && <Alert color="warning">{t('accessSetup.ssoRequiresHttps')}</Alert>}
            <section className={panelClassName} aria-labelledby="access-test-owner-title">
              <h4 id="access-test-owner-title" className="font-semibold text-themed-primary">
                {t('accessSetup.ownerHeading')}
              </h4>
              <p className="text-sm text-themed-secondary">{t('accessSetup.testOwner')}</p>
              {ownerKeyField}
              <p className="text-xs text-themed-muted">{t('accessSetup.testRecovery')}</p>
            </section>
          </>
        )}

        {error && (
          <div role="alert" ref={errorMessage} tabIndex={-1}>
            <Alert color="error">{error}</Alert>
          </div>
        )}
      </div>
    </SetupGate>
  );
}
