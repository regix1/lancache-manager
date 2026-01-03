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
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import ApiService from '@services/api.service';
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
  isAuthenticated: boolean;
  mockMode: boolean;
}

interface FileItemRowProps {
  item: FileSystemItem;
  selectedFile: string | null;
  onItemClick: (item: FileSystemItem) => void;
  isRootLevel?: boolean;
}

const FileItemRow: React.FC<FileItemRowProps> = ({ item, selectedFile, onItemClick, isRootLevel }) => {
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
            {formatSize(item.size)} • {formattedLastModified}
          </div>
        )}
      </div>

      {item.isDirectory && item.isAccessible && (
        <ChevronRight className="w-4 h-4 text-themed-muted flex-shrink-0" />
      )}
    </button>
  );
};

const formatSize = (bytes: number): string => {
  if (bytes === 0) return '-';
  const units = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  return `${(bytes / Math.pow(1024, i)).toFixed(2)} ${units[i]}`;
};

const FileBrowser: React.FC<FileBrowserProps> = ({ onSelectFile, isAuthenticated, mockMode }) => {
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
    if (!mockMode && isAuthenticated) {
      loadDirectory(null);
    }
  }, [mockMode, isAuthenticated]);

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
      setError((err instanceof Error ? err.message : String(err)) || 'Failed to load directory');
    } finally {
      setLoading(false);
    }
  };

  const handleItemClick = (item: FileSystemItem) => {
    if (!item.isAccessible) {
      setError('Access denied to this location');
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
        setError('No database files found in this location');
      }
    } catch (err: unknown) {
      setError((err instanceof Error ? err.message : String(err)) || 'Search failed');
    } finally {
      setSearching(false);
    }
  };

  const displayItems = searchResults.length > 0 ? searchResults : items;

  return (
    <div className="space-y-3">
      {/* Navigation Bar */}
      <div
        className="flex items-center gap-2 p-2 rounded-lg"
        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
      >
        <Button
          onClick={handleGoHome}
          size="xs"
          variant={isRootLevel && !currentPath ? 'filled' : 'default'}
          color="blue"
          disabled={loading || mockMode}
          title="Home"
        >
          <Home className="w-3.5 h-3.5" />
        </Button>

        {currentPath && currentPath !== '/' && (
          <Button
            onClick={handleBack}
            size="xs"
            variant="default"
            disabled={loading || mockMode}
            title="Go back"
          >
            <ArrowLeft className="w-3.5 h-3.5" />
          </Button>
        )}

        {/* Breadcrumb Path */}
        <div className="flex-1 min-w-0">
          <div
            className="text-xs font-mono px-2 py-1 rounded truncate"
            style={{ backgroundColor: 'var(--theme-bg-secondary)', color: 'var(--theme-text-secondary)' }}
          >
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
            title="Search for .db files"
          >
            {searching ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Search className="w-3.5 h-3.5" />}
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
        <div
          className="rounded-lg border overflow-hidden"
          style={{ borderColor: 'var(--theme-border-secondary)' }}
        >
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
        <div
          className="text-center py-10 rounded-lg"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          <Folder className="w-10 h-10 mx-auto mb-2 text-themed-muted opacity-50" />
          <p className="text-sm text-themed-muted">No directories or .db files found</p>
        </div>
      )}

      {/* Selected File Display */}
      {selectedFile && (
        <div
          className="p-3 rounded-lg border"
          style={{
            backgroundColor: 'var(--theme-success-bg)',
            borderColor: 'var(--theme-success)'
          }}
        >
          <div className="flex items-center gap-3">
            <div
              className="flex-shrink-0 w-10 h-10 rounded-lg flex items-center justify-center"
              style={{ backgroundColor: 'var(--theme-success)', opacity: 0.15 }}
            >
              <Database className="w-5 h-5" style={{ color: 'var(--theme-success)' }} />
            </div>
            <div className="flex-1 min-w-0">
              <p className="text-xs text-themed-muted mb-0.5">Selected database</p>
              <p className="text-sm font-mono text-themed-primary truncate">{selectedFile}</p>
            </div>
            <Button
              onClick={handleSelectFile}
              size="sm"
              variant="filled"
              color="green"
            >
              Use File
            </Button>
          </div>
        </div>
      )}

      {/* Search Results Info */}
      {searchResults.length > 0 && (
        <div className="flex items-center justify-between text-sm">
          <span className="text-themed-secondary">
            Found <strong>{searchResults.length}</strong> database file(s)
          </span>
          <Button
            onClick={() => setSearchResults([])}
            size="xs"
            variant="default"
          >
            Clear
          </Button>
        </div>
      )}
    </div>
  );
};

export default FileBrowser;
