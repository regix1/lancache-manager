import React, { useState, useEffect } from 'react';
import { 
  Card, 
  CardBody, 
  CardHeader,
  Button, 
  Progress,
  Divider,
  Chip,
  Modal,
  ModalContent,
  ModalHeader,
  ModalBody,
  ModalFooter,
  useDisclosure,
  Spinner
} from '@heroui/react';
import { Database, HardDrive, Trash2, AlertTriangle, RotateCcw, FileText, Loader } from 'lucide-react';
import axios from 'axios';

function Management() {
  const [cacheInfo, setCacheInfo] = useState(null);
  const [loading, setLoading] = useState(false);
  const [processing, setProcessing] = useState(false);
  const [message, setMessage] = useState('');
  const [loadError, setLoadError] = useState(false);
  const {isOpen, onOpen, onOpenChange} = useDisclosure();
  const [modalAction, setModalAction] = useState(null);

  useEffect(() => {
    loadCacheInfo();
  }, []);

  const loadCacheInfo = async () => {
    try {
      setLoadError(false);
      const response = await axios.get('/api/management/cache', {
        timeout: 5000 // 5 second timeout
      });
      setCacheInfo(response.data);
    } catch (error) {
      console.error('Error loading cache info:', error);
      setLoadError(true);
      // Set some default data so the UI isn't broken
      setCacheInfo({
        totalCacheSize: 0,
        usedCacheSize: 0,
        freeCacheSize: 0,
        totalFiles: 0,
        serviceSizes: {}
      });
    }
  };

  const handleClearCache = async (service = null) => {
    setModalAction({ type: 'clearCache', service });
    onOpen();
  };

  const handleResetDatabase = async () => {
    setModalAction({ type: 'resetDatabase' });
    onOpen();
  };

  const handleResetLogs = async () => {
    setModalAction({ type: 'resetLogs' });
    onOpen();
  };

  const handleProcessAllLogs = async () => {
    setModalAction({ type: 'processLogs' });
    onOpen();
  };

  const executeAction = async () => {
    onOpenChange(false);
    setLoading(true);
    
    try {
      switch (modalAction?.type) {
        case 'clearCache':
          await axios.delete(`/api/management/cache${modalAction.service ? `?service=${modalAction.service}` : ''}`);
          setMessage(`Cache cleared for ${modalAction.service || 'all services'}`);
          break;
        case 'resetDatabase':
          await axios.delete('/api/management/database');
          setMessage('Database reset successfully');
          break;
        case 'resetLogs':
          const resetResponse = await axios.post('/api/management/reset-logs');
          setMessage(resetResponse.data.message);
          setTimeout(() => window.location.reload(), 3000);
          break;
        case 'processLogs':
          setProcessing(true);
          const processResponse = await axios.post('/api/management/process-all-logs');
          const { logSizeMB, estimatedTimeMinutes } = processResponse.data;
          setMessage(
            `Processing ${logSizeMB.toFixed(1)} MB log file. ` +
            `Estimated time: ${estimatedTimeMinutes} minutes. ` +
            `The page will refresh when complete.`
          );
          setTimeout(() => window.location.reload(), estimatedTimeMinutes * 60 * 1000);
          break;
      }
      await loadCacheInfo();
    } catch (error) {
      setMessage('Error: ' + error.message);
    }
    
    setLoading(false);
    setTimeout(() => setMessage(''), 5000);
  };

  const formatBytes = (bytes) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  const getModalContent = () => {
    switch (modalAction?.type) {
      case 'clearCache':
        return {
          title: modalAction.service ? `Clear ${modalAction.service} Cache` : 'Clear All Cache',
          body: modalAction.service 
            ? `Are you sure you want to clear the cache for ${modalAction.service}?`
            : 'Are you sure you want to clear ALL cache? This will delete all cached game files!',
          color: 'warning'
        };
      case 'resetDatabase':
        return {
          title: 'Reset Database',
          body: 'Are you sure you want to reset the database? This will delete all download history and statistics!',
          color: 'danger'
        };
      case 'resetLogs':
        return {
          title: 'Reset Log Position',
          body: 'This will clear all download history, reset statistics, and start monitoring from the current end of the log file. Only NEW downloads will be tracked going forward.',
          color: 'primary'
        };
      case 'processLogs':
        return {
          title: 'Process Entire Log File',
          body: 'WARNING: This will process your ENTIRE log file from the beginning! This can take a VERY long time (10+ minutes for large logs) and create thousands of database entries.',
          color: 'secondary'
        };
      default:
        return { title: '', body: '', color: 'default' };
    }
  };

  return (
    <div className="space-y-6">
      <h2 className="text-2xl font-semibold">System Management</h2>

      {/* Status Messages */}
      {message && (
        <Card className={message.includes('Error') ? 'bg-danger-50' : message.includes('Processing') ? 'bg-primary-50' : 'bg-success-50'}>
          <CardBody className="flex flex-row items-center gap-3">
            {message.includes('Processing') && <Spinner size="sm" />}
            <p className="font-medium">{message}</p>
          </CardBody>
        </Card>
      )}

      {/* Processing Indicator */}
      {processing && (
        <Card className="bg-primary-50">
          <CardBody>
            <div className="flex items-center gap-3">
              <Spinner color="primary" />
              <div>
                <p className="font-semibold">Processing Log File</p>
                <p className="text-sm text-default-500">This may take several minutes. Please wait...</p>
              </div>
            </div>
            <Progress 
              size="sm"
              isIndeterminate
              aria-label="Processing..."
              className="mt-3"
              color="primary"
            />
          </CardBody>
        </Card>
      )}

      {/* Cache Information */}
      <Card>
        <CardHeader className="flex items-center gap-2">
          <HardDrive className="h-5 w-5 text-default-500" />
          <h3 className="text-lg font-semibold">Cache Storage</h3>
          {loadError && (
            <Chip color="warning" size="sm" className="ml-auto">
              Using Default Values
            </Chip>
          )}
        </CardHeader>
        <Divider />
        <CardBody>
          {cacheInfo ? (
            <div className="space-y-6">
              <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                <div>
                  <p className="text-sm text-default-500">Total Size</p>
                  <p className="text-xl font-semibold">{formatBytes(cacheInfo.totalCacheSize)}</p>
                </div>
                <div>
                  <p className="text-sm text-default-500">Used</p>
                  <p className="text-xl font-semibold">{formatBytes(cacheInfo.usedCacheSize)}</p>
                </div>
                <div>
                  <p className="text-sm text-default-500">Free</p>
                  <p className="text-xl font-semibold">{formatBytes(cacheInfo.freeCacheSize)}</p>
                </div>
                <div>
                  <p className="text-sm text-default-500">Total Files</p>
                  <p className="text-xl font-semibold">{cacheInfo.totalFiles.toLocaleString()}</p>
                </div>
              </div>
              
              {Object.keys(cacheInfo.serviceSizes).length > 0 && (
                <>
                  <Divider />
                  <div>
                    <h4 className="font-semibold mb-3">Service Breakdown</h4>
                    <div className="space-y-2">
                      {Object.entries(cacheInfo.serviceSizes).map(([service, size]) => (
                        <div key={service} className="flex justify-between items-center">
                          <span className="text-default-600 capitalize">{service}</span>
                          <div className="flex items-center gap-3">
                            <span className="font-mono text-sm">{formatBytes(size)}</span>
                            <Button
                              isIconOnly
                              size="sm"
                              color="danger"
                              variant="light"
                              onPress={() => handleClearCache(service)}
                              isDisabled={loading}
                            >
                              <Trash2 className="h-4 w-4" />
                            </Button>
                          </div>
                        </div>
                      ))}
                    </div>
                  </div>
                </>
              )}
            </div>
          ) : (
            <div className="flex justify-center py-8">
              <Spinner label="Loading cache information..." />
            </div>
          )}
        </CardBody>
      </Card>

      {/* Action Buttons */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <Button
          color="warning"
          startContent={<Trash2 className="h-5 w-5" />}
          onPress={() => handleClearCache()}
          isDisabled={loading || processing}
          className="font-medium"
        >
          Clear All Cache
        </Button>

        <Button
          color="danger"
          startContent={<Database className="h-5 w-5" />}
          onPress={handleResetDatabase}
          isDisabled={loading || processing}
          className="font-medium"
        >
          Reset Database
        </Button>

        <Button
          color="primary"
          startContent={<RotateCcw className="h-5 w-5" />}
          onPress={handleResetLogs}
          isDisabled={loading || processing}
          className="font-medium"
        >
          Reset Log Position
        </Button>

        <Button
          color="secondary"
          startContent={<FileText className="h-5 w-5" />}
          onPress={handleProcessAllLogs}
          isDisabled={loading || processing}
          className="font-medium"
        >
          Process Entire Log
        </Button>
      </div>

      {/* Warning Box */}
      <Card className="bg-warning-50 border-warning">
        <CardBody>
          <div className="flex gap-3">
            <AlertTriangle className="h-5 w-5 text-warning flex-shrink-0 mt-0.5" />
            <div className="text-sm">
              <p className="font-semibold mb-2">Important Notes:</p>
              <ul className="space-y-1 list-disc list-inside">
                <li><strong>Reset Log Position:</strong> Starts fresh from current log position, only tracking new downloads</li>
                <li><strong>Process Entire Log:</strong> Imports ALL historical data (can take very long for large logs)</li>
                <li><strong>Clear Cache:</strong> Deletes actual cached game files (clients will need to re-download)</li>
                <li><strong>Reset Database:</strong> Clears all statistics and history (keeps cache files)</li>
              </ul>
            </div>
          </div>
        </CardBody>
      </Card>

      {/* Confirmation Modal */}
      <Modal 
        isOpen={isOpen} 
        onOpenChange={onOpenChange}
        backdrop="blur"
      >
        <ModalContent>
          {(onClose) => {
            const content = getModalContent();
            return (
              <>
                <ModalHeader className="flex flex-col gap-1">
                  {content.title}
                </ModalHeader>
                <ModalBody>
                  <p>{content.body}</p>
                </ModalBody>
                <ModalFooter>
                  <Button color="default" variant="light" onPress={onClose}>
                    Cancel
                  </Button>
                  <Button color={content.color} onPress={executeAction}>
                    Confirm
                  </Button>
                </ModalFooter>
              </>
            );
          }}
        </ModalContent>
      </Modal>
    </div>
  );
}

export default Management;