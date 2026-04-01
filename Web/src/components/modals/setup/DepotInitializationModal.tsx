import React from 'react';
import { Rocket, ArrowLeft, Loader2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useInitializationFlow, type InitStep } from '@hooks/useInitializationFlow';
import {
  DatabaseSetupStep,
  PermissionsCheckStep,
  ImportHistoricalDataStep,
  PlatformSetupStep,
  EpicAuthStep,
  SteamApiKeyStep,
  SteamPicsAuthStep,
  DepotInitStep,
  PicsProgressStep,
  LogProcessingStep
} from '../../initialization/steps';

interface DepotInitializationModalProps {
  onInitialized: () => void;
}

const DepotInitializationModal: React.FC<DepotInitializationModalProps> = ({ onInitialized }) => {
  const { t } = useTranslation();

  const {
    currentStep,
    stepInfo,
    dataSourceChoice,
    syncError,
    isCheckingAuth,
    picsData,
    usingSteamAuth,
    backButtonDisabled,
    setBackButtonDisabled,
    completedPlatforms,
    handleGoBack,
    handleDatabaseSetupComplete,
    handlePermissionsCheckComplete,
    handleImportComplete,
    handleSelectPlatform,
    handlePlatformContinue,
    handlePlatformSkip,
    handleEpicAuthComplete,
    handleEpicAuthSkip,
    handleSteamApiKeyComplete,
    handleSteamAuthComplete,
    handleDepotInitComplete,
    handleDepotInitGenerateOwn,
    handleDepotInitContinue,
    handlePicsProgressComplete,
    handlePicsProgressCancel,
    handleLogProcessingComplete,
    handleLogProcessingSkip,
    handleBackToSteamAuth
  } = useInitializationFlow({ onInitialized });

  const renderStep = (step: InitStep): React.ReactNode => {
    // Show loading state while hydrating wizard state from the server.
    if (isCheckingAuth && step !== 'database-setup') {
      return (
        <div className="flex flex-col items-center justify-center py-12">
          <Loader2 className="w-12 h-12 animate-spin icon-primary mb-4" />
          <p className="text-themed-secondary">{t('common.loading', 'Loading...')}</p>
        </div>
      );
    }

    switch (step) {
      case 'database-setup':
        return <DatabaseSetupStep onSetupComplete={handleDatabaseSetupComplete} />;

      case 'permissions-check':
        return <PermissionsCheckStep onComplete={handlePermissionsCheckComplete} />;

      case 'import-historical-data':
        return (
          <ImportHistoricalDataStep
            onComplete={handleImportComplete}
            onSkip={handleImportComplete}
          />
        );

      case 'platform-setup':
        return (
          <PlatformSetupStep
            onSelectPlatform={handleSelectPlatform}
            onContinue={handlePlatformContinue}
            onSkip={handlePlatformSkip}
            completedPlatforms={completedPlatforms}
          />
        );

      case 'steam-api-key':
        return <SteamApiKeyStep onComplete={handleSteamApiKeyComplete} />;

      case 'steam-auth':
        return <SteamPicsAuthStep onComplete={handleSteamAuthComplete} />;

      case 'depot-init':
        return (
          <DepotInitStep
            picsData={picsData}
            usingSteamAuth={usingSteamAuth}
            hideOptions={dataSourceChoice === 'github'}
            onGenerateOwn={handleDepotInitGenerateOwn}
            onContinue={handleDepotInitContinue}
            onComplete={handleDepotInitComplete}
            onBackToSteamAuth={handleBackToSteamAuth}
          />
        );

      case 'pics-progress':
        return (
          <PicsProgressStep
            onComplete={handlePicsProgressComplete}
            onProcessingStateChange={setBackButtonDisabled}
            onCancel={handlePicsProgressCancel}
          />
        );

      case 'epic-auth':
        return (
          <EpicAuthStep
            onComplete={handleEpicAuthComplete}
            onSkip={handleEpicAuthSkip}
            onAuthStateChange={setBackButtonDisabled}
          />
        );

      case 'log-processing':
        return (
          <LogProcessingStep
            onComplete={handleLogProcessingComplete}
            onSkip={handleLogProcessingSkip}
            onProcessingStateChange={setBackButtonDisabled}
          />
        );

      default:
        return null;
    }
  };

  return (
    <div className="fixed inset-0 z-[80] flex items-center justify-center p-4 bg-themed-primary">
      {/* Stripe background pattern */}
      <div
        className="absolute inset-0 opacity-5"
        style={{
          backgroundImage: `repeating-linear-gradient(45deg, transparent, transparent 35px, var(--theme-text-primary) 35px, var(--theme-text-primary) 70px)`
        }}
      />

      {/* Main Card */}
      <div className="relative z-10 w-full max-w-4xl rounded-xl border overflow-hidden flex flex-col bg-themed-secondary border-themed-primary max-h-[calc(100vh-2rem)]">
        {/* Header */}
        <div className="px-8 py-5 border-b flex items-center justify-between border-themed-secondary">
          <div className="flex items-center gap-3">
            {currentStep !== 'database-setup' && (
              <button
                onClick={backButtonDisabled ? undefined : handleGoBack}
                disabled={backButtonDisabled}
                className={`p-1.5 rounded-lg transition-colors bg-transparent ${
                  backButtonDisabled
                    ? 'text-themed-muted cursor-not-allowed opacity-50'
                    : 'text-themed-secondary cursor-pointer'
                }`}
                title={
                  backButtonDisabled
                    ? t('initialization.modal.cannotGoBack')
                    : t('initialization.modal.goBack')
                }
              >
                <ArrowLeft className="w-5 h-5" />
              </button>
            )}
            <div className="flex items-center gap-2">
              <Rocket className="w-5 h-5 text-primary" />
              <span className="font-semibold text-themed-primary">
                {t('initialization.modal.setupWizard')}
              </span>
            </div>
          </div>
          <div className="text-xs font-medium px-2.5 py-1 rounded-full bg-themed-tertiary text-themed-secondary">
            {stepInfo.number} / {stepInfo.total}
          </div>
        </div>

        {/* Progress Bar */}
        <div className="h-1 bg-themed-tertiary">
          <div
            className="h-full transition-all duration-300 ease-out bg-primary"
            style={{ width: `${(stepInfo.number / stepInfo.total) * 100}%` }}
          />
        </div>

        {/* Content */}
        <div className="p-8">
          {syncError && (
            <div className="mb-4 p-3 rounded-lg bg-themed-error">
              <p className="text-sm text-themed-error">{syncError}</p>
            </div>
          )}
          {renderStep(currentStep)}
        </div>
      </div>
    </div>
  );
};

export default DepotInitializationModal;
