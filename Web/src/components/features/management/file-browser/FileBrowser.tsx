import React, { useState, useEffect } from 'react';
import {
  Folder,
  File,
  ChevronRight,
  ArrowLeft,
  Home,
  Loader2,
  Search,
  HardDrive,
  Database
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import ApiService from '@services/api.service';
import { formatBytes } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';

interface FileSystemItem {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number;
  lastModified: string;
  isAccessible: boolean;
}

interface DirectoryListing {
  currentPath: string;
  parentPath: string | null;
  items: FileSystemItem[];
}

interface FileBrowserProps {
  onSelectFile: (path: string) => void;
  isAdmin: boolean;
  mockMode: boolean;
}

interface FileItemRowProps {
  item: FileSystemItem;
  selectedFile: string | null;
  onItemClick: (item: FileSystemItem) => void;
  isRootLevel?: boolean;
}

const FileItemRow: React.FC<FileItemRowProps> = ({
  item,
  selectedFile,
  onItemClick,
  isRootLevel
}) => {
  const formattedLastModified = useFormattedDateTime(item.lastModified);
  const isSelected = selectedFile === item.path;
  const isDrive = isRootLevel && item.isDirectory;
  const isDbFile = !item.isDirectory && item.name.endsWith('.db');

  return (
    <button
      onClick={() => onItemClick(item)}
      disabled={!item.isAccessible}
      className={`w-full px-3 py-2.5 flex items-center gap-3 transition-all text-left rounded-lg
        ${!item.isAccessible ? 'opacity-40 cursor-not-allowed' : 'hover:bg-themed-hover cursor-pointer'}
        ${isSelected ? 'bg-themed-accent-subtle ring-1 ring-themed-accent' : ''}
      `}
    >
      <div
        className={`flex-shrink-0 w-9 h-9 rounded-lg flex items-center justify-center ${
          isDrive
            ? 'icon-bg-blue'
            : item.isDirectory
              ? 'icon-bg-orange'
              : isDbFile
                ? 'icon-bg-green'
                : 'bg-themed-tertiary'
        }`}
      >
        {isDrive ? (
          <HardDrive className="w-4 h-4 icon-blue" />
        ) : item.isDirectory ? (
          <Folder className="w-4 h-4 icon-orange" />
        ) : isDbFile ? (
          <Database className="w-4 h-4 icon-green" />
        ) : (
          <File className="w-4 h-4 text-themed-secondary" />
        )}
      </div>

      <div className="flex-1 min-w-0">
        <div className="font-medium text-themed-primary truncate text-sm">{item.name}</div>
        {!item.isDirectory && (
          <div className="text-xs text-themed-muted mt-0.5">
            {formatBytes(item.size, 2, '-')} • {formattedLastModified}
          </div>
        )}
      </div>

      {item.isDirectory && item.isAccessible && (
        <ChevronRight className="w-4 h-4 text-themed-muted flex-shrink-0" />
      )}
    </button>
  );
};

const FileBrowser: React.FC<FileBrowserProps> = ({ onSelectFile, isAdmin, mockMode }) => {
  const { t } = useTranslation();
  const [currentPath, setCurrentPath] = useState<string | null>(null);
  const [items, setItems] = useState<FileSystemItem[]>([]);
  const [parentPath, setParentPath] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedFile, setSelectedFile] = useState<string | null>(null);
  const [searching, setSearching] = useState(false);
  const [searchResults, setSearchResults] = useState<FileSystemItem[]>([]);

  // Root level is when we're showing the list of allowed paths (home view)
  const isRootLevel = currentPath === '/' || currentPath === null;

  useEffect(() => {
    if (!mockMode && isAdmin) {
      loadDirectory(null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode, isAdmin]);

  const loadDirectory = async (path: string | null) => {
    setLoading(true);
    setError(null);
    setSearchResults([]);

    try {
      const queryParam = path ? `?path=${encodeURIComponent(path)}` : '';
      const res = await fetch(
        `/api/filebrowser/list${queryParam}`,
        ApiService.getFetchOptions({ method: 'GET' })
      );

      const result = await ApiService.handleResponse<DirectoryListing>(res);
      setCurrentPath(result.currentPath);
      setParentPath(result.parentPath);
      setItems(result.items);
    } catch (err: unknown) {
      setError(
        (err instanceof Error ? err.message : String(err)) ||
          t('management.fileBrowser.failedToLoadDirectory')
      );
    } finally {
      setLoading(false);
    }
  };

  const handleItemClick = (item: FileSystemItem) => {
    if (!item.isAccessible) {
      setError(t('management.fileBrowser.accessDenied'));
      return;
    }

    if (item.isDirectory) {
      loadDirectory(item.path);
    } else {
      setSelectedFile(item.path);
    }
  };

  const handleBack = () => {
    if (parentPath !== null) {
      loadDirectory(parentPath);
    } else {
      // Parent is outside allowed paths, go to home (root listing of allowed paths)
      loadDirectory(null);
    }
  };

  const handleGoHome = () => {
    loadDirectory(null);
  };

  const handleSelectFile = () => {
    if (selectedFile) {
      onSelectFile(selectedFile);
    }
  };

  const handleSearch = async (searchPath: string) => {
    setSearching(true);
    setError(null);

    try {
      const res = await fetch(
        `/api/filebrowser/search?searchPath=${encodeURIComponent(searchPath)}`,
        ApiService.getFetchOptions({ method: 'GET' })
      );

      const result = await ApiService.handleResponse<{ results: FileSystemItem[] }>(res);
      setSearchResults(result.results);

      if (result.results.length === 0) {
        setError(t('management.fileBrowser.noDatabaseFiles'));
      }
    } catch (err: unknown) {
      setError(
        (err instanceof Error ? err.message : String(err)) ||
          t('management.fileBrowser.searchFailed')
      );
    } finally {
      setSearching(false);
    }
  };

  const displayItems = searchResults.length > 0 ? searchResults : items;

  return (
    <div className="space-y-3">
      {/* Navigation Bar */}
      <div className="flex items-center gap-2 p-2 rounded-lg bg-themed-tertiary">
        <Button
          onClick={handleGoHome}
          size="xs"
          variant={isRootLevel && !currentPath ? 'filled' : 'default'}
          color="blue"
          disabled={loading || mockMode}
          title={t('management.fileBrowser.home')}
        >
          <Home className="w-3.5 h-3.5" />
        </Button>

        {currentPath && currentPath !== '/' && (
          <Button
            onClick={handleBack}
            size="xs"
            variant="default"
            disabled={loading || mockMode}
            title={t('management.fileBrowser.goBack')}
          >
            <ArrowLeft className="w-3.5 h-3.5" />
          </Button>
        )}

        {/* Breadcrumb Path */}
        <div className="flex-1 min-w-0">
          <div className="text-xs font-mono px-2 py-1 rounded truncate bg-themed-secondary text-themed-secondary">
            {currentPath || '/'}
          </div>
        </div>

        {/* Search Button */}
        {currentPath && currentPath !== '/' && (
          <Button
            onClick={() => handleSearch(currentPath)}
            size="xs"
            variant="default"
            disabled={searching || loading || mockMode}
            title={t('management.fileBrowser.searchForDb')}
          >
            {searching ? (
              <Loader2 className="w-3.5 h-3.5 animate-spin" />
            ) : (
              <Search className="w-3.5 h-3.5" />
            )}
          </Button>
        )}
      </div>

      {/* Error */}
      {error && (
        <Alert color="red">
          <span className="text-sm">{error}</span>
        </Alert>
      )}

      {/* Loading */}
      {loading && (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-themed-accent" />
        </div>
      )}

      {/* Items List */}
      {!loading && displayItems.length > 0 && (
        <div className="rounded-lg border overflow-hidden border-themed-secondary">
          <CustomScrollbar maxHeight="280px">
            <div className="p-1.5 space-y-0.5">
              {displayItems.map((item, index) => (
                <FileItemRow
                  key={index}
                  item={item}
                  selectedFile={selectedFile}
                  onItemClick={handleItemClick}
                  isRootLevel={isRootLevel}
                />
              ))}
            </div>
          </CustomScrollbar>
        </div>
      )}

      {/* Empty State */}
      {!loading && displayItems.length === 0 && !error && currentPath && (
        <div className="text-center py-10 rounded-lg bg-themed-tertiary">
          <Folder className="w-10 h-10 mx-auto mb-2 text-themed-muted opacity-50" />
          <p className="text-sm text-themed-muted">{t('management.fileBrowser.noFilesFound')}</p>
        </div>
      )}

      {/* Selected File Display */}
      {selectedFile && (
        <div className="p-3 rounded-lg border bg-themed-success border-themed-success">
          <div className="flex items-center gap-3">
            <div className="flex-shrink-0 w-10 h-10 rounded-lg flex items-center justify-center bg-themed-success/15">
              <Database className="w-5 h-5 icon-success" />
            </div>
            <div className="flex-1 min-w-0">
              <p className="text-xs text-themed-muted mb-0.5">
                {t('management.fileBrowser.selectedDatabase')}
              </p>
              <p className="text-sm font-mono text-themed-primary truncate">{selectedFile}</p>
            </div>
            <Button onClick={handleSelectFile} size="sm" variant="filled" color="green">
              {t('management.fileBrowser.useFile')}
            </Button>
          </div>
        </div>
      )}

      {/* Search Results Info */}
      {searchResults.length > 0 && (
        <div className="flex items-center justify-between text-sm">
          <span className="text-themed-secondary">
            {t('management.fileBrowser.foundFiles', { count: searchResults.length })}
          </span>
          <Button onClick={() => setSearchResults([])} size="xs" variant="default">
            {t('common.clear')}
          </Button>
        </div>
      )}
    </div>
  );
};

export default FileBrowser;
