import React, { useState, useEffect, useRef } from 'react';
import { Palette, Upload, Trash2, Check, X, Download, Loader, Eye, RefreshCw, Lock } from 'lucide-react';
import themeService from '../../services/theme.service';

const ThemeManager = ({ isAuthenticated }) => {
    const [themes, setThemes] = useState([]);
    const [currentTheme, setCurrentTheme] = useState('dark-default');
    const [loading, setLoading] = useState(false);
    const [dragActive, setDragActive] = useState(false);
    const [uploadError, setUploadError] = useState(null);
    const [uploadSuccess, setUploadSuccess] = useState(null);
    const [previewTheme, setPreviewTheme] = useState(null);
    const fileInputRef = useRef(null);

    useEffect(() => {
        loadThemes();
        setCurrentTheme(themeService.getCurrentThemeId());
    }, []);

    const loadThemes = async () => {
        setLoading(true);
        try {
            const themeList = await themeService.loadThemes();
            setThemes(themeList);
        } catch (error) {
            console.error('Failed to load themes:', error);
            setUploadError('Failed to load themes');
        } finally {
            setLoading(false);
        }
    };

    const handleThemeChange = async (themeId) => {
        try {
            const theme = await themeService.getTheme(themeId);
            if (theme) {
                themeService.applyTheme(theme);
                setCurrentTheme(themeId);
                setPreviewTheme(null);
            }
        } catch (error) {
            console.error('Failed to apply theme:', error);
            setUploadError('Failed to apply theme');
        }
    };

    const handlePreview = async (themeId) => {
        if (previewTheme === themeId) {
            // Stop preview, return to current theme
            handleThemeChange(currentTheme);
            setPreviewTheme(null);
        } else {
            // Preview this theme
            try {
                const theme = await themeService.getTheme(themeId);
                if (theme) {
                    themeService.applyTheme(theme);
                    setPreviewTheme(themeId);
                }
            } catch (error) {
                console.error('Failed to preview theme:', error);
            }
        }
    };

    const handleDrag = (e) => {
        e.preventDefault();
        e.stopPropagation();
        if (e.type === "dragenter" || e.type === "dragover") {
            setDragActive(true);
        } else if (e.type === "dragleave") {
            setDragActive(false);
        }
    };

    const handleDrop = async (e) => {
        e.preventDefault();
        e.stopPropagation();
        setDragActive(false);

        if (e.dataTransfer.files && e.dataTransfer.files[0]) {
            await handleFile(e.dataTransfer.files[0]);
        }
    };

    const handleFile = async (file) => {
        setUploadError(null);
        setUploadSuccess(null);

        if (!file.name.endsWith('.json') && !file.name.endsWith('.toml')) {
            setUploadError('Only JSON and TOML theme files are allowed');
            return;
        }

        if (file.size > 1024 * 1024) {
            setUploadError('Theme file too large (max 1MB)');
            return;
        }

        setLoading(true);
        try {
            const result = await themeService.uploadTheme(file);
            setUploadSuccess(`Theme "${file.name}" uploaded successfully`);
            await loadThemes();

            setTimeout(() => setUploadSuccess(null), 5000);
        } catch (error) {
            setUploadError(error.message);
        } finally {
            setLoading(false);
        }
    };

    const handleDelete = async (themeId, themeName, isDefault) => {
        // Check if it's a system theme first
        if (isDefault) {
            setUploadError('System themes cannot be deleted. These are built-in themes required for the application.');
            setTimeout(() => setUploadError(null), 5000);
            return;
        }

        if (!isAuthenticated) {
            setUploadError('Authentication required to delete custom themes');
            setTimeout(() => setUploadError(null), 5000);
            return;
        }

        if (!confirm(`Delete theme "${themeName}"? This cannot be undone.`)) return;

        setLoading(true);
        try {
            await themeService.deleteTheme(themeId);
            await loadThemes();

            if (currentTheme === themeId) {
                handleThemeChange('dark-default');
            }

            setUploadSuccess(`Theme "${themeName}" deleted successfully`);
            setTimeout(() => setUploadSuccess(null), 5000);
        } catch (error) {
            setUploadError(error.message);
        } finally {
            setLoading(false);
        }
    };

    const downloadSampleTheme = () => {
        // Download sample TOML theme
        const sampleToml = `# Sample Theme for LanCache Monitor
# Edit the values below to customize your theme

[meta]
name = "My Custom Theme"
id = "my-custom-theme"
description = "A beautiful custom theme"
author = "Your Name"
version = "1.0.0"

# ===================================
# Background Colors
# ===================================
[colors]
"--bg-primary" = "#0f172a"      # Main background
"--bg-secondary" = "#1e293b"    # Card backgrounds
"--bg-tertiary" = "#334155"     # Input backgrounds
"--bg-hover" = "#475569"        # Hover states
"--bg-input" = "#334155"        # Form inputs
"--bg-dropdown" = "#1e293b"     # Dropdown menus
"--bg-dropdown-hover" = "#334155"
"--bg-nav" = "#1e293b"          # Navigation bar

# ===================================
# Borders
# ===================================
"--border-primary" = "#334155"
"--border-secondary" = "#475569"
"--border-input" = "#475569"
"--border-nav" = "#334155"
"--border-dropdown" = "#334155"

# ===================================
# Text Colors
# ===================================
"--text-primary" = "#f8fafc"    # Main text
"--text-secondary" = "#cbd5e1"  # Secondary text
"--text-muted" = "#94a3b8"      # Muted/disabled text
"--text-disabled" = "#64748b"   # Disabled state
"--text-button" = "#ffffff"     # Button text
"--text-dropdown" = "#f8fafc"
"--text-dropdown-item" = "#f8fafc"
"--text-input" = "#f8fafc"
"--text-placeholder" = "#94a3b8"
"--text-nav" = "#cbd5e1"
"--text-nav-active" = "#0ea5e9"

# ===================================
# Icons
# ===================================
"--icon-primary" = "#cbd5e1"
"--icon-button" = "#ffffff"
"--icon-muted" = "#94a3b8"

# ===================================
# Accent Colors
# ===================================
"--accent-blue" = "#0ea5e9"
"--accent-green" = "#10b981"
"--accent-yellow" = "#f59e0b"
"--accent-red" = "#ef4444"
"--accent-purple" = "#a855f7"
"--accent-cyan" = "#06b6d4"
"--accent-orange" = "#f97316"
"--accent-pink" = "#ec4899"

# ===================================
# Status Colors
# ===================================
"--success" = "#10b981"
"--warning" = "#f59e0b"
"--error" = "#ef4444"
"--info" = "#0ea5e9"

# ===================================
# Optional: Custom CSS
# ===================================
[css]
content = """
/* Add any custom CSS here */
/* Example: Make all buttons rounded */
button {
  border-radius: 0.5rem;
}
"""`;

        const blob = new Blob([sampleToml], { type: 'text/plain' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'sample-theme.toml';
        a.click();
        URL.revokeObjectURL(url);
    };

    return (
        <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
            <div className="flex items-center justify-between mb-4">
                <div className="flex items-center space-x-2">
                    <Palette className="w-5 h-5 text-purple-400" />
                    <h3 className="text-lg font-semibold text-white">Theme Management</h3>
                </div>
                <button
                    onClick={loadThemes}
                    disabled={loading}
                    className="p-2 text-gray-400 hover:text-gray-200 hover:bg-gray-700 rounded-lg transition-colors"
                    title="Refresh themes"
                >
                    <RefreshCw className={`w-4 h-4 ${loading ? 'animate-spin' : ''}`} />
                </button>
            </div>

            {/* Current Theme Selector */}
            <div className="mb-6">
                <label className="block text-sm font-medium text-gray-300 mb-2">
                    Active Theme
                </label>
                <select
                    value={previewTheme || currentTheme}
                    onChange={(e) => handleThemeChange(e.target.value)}
                    className="w-full bg-gray-700 text-white rounded px-3 py-2 border border-gray-600 focus:border-purple-500 focus:outline-none"
                    disabled={loading}
                >
                    {themes.map(theme => (
                        <option key={theme.id} value={theme.id}>
                            {theme.name} {theme.author && theme.author !== 'System' && `by ${theme.author}`}
                            {theme.isDefault && ' (System)'}
                            {theme.format === 'toml' && ' [TOML]'}
                            {previewTheme === theme.id && ' (Preview)'}
                        </option>
                    ))}
                </select>
                {previewTheme && (
                    <p className="text-xs text-yellow-400 mt-2">
                        Preview mode active. Select a theme to apply it permanently.
                    </p>
                )}
            </div>

            {/* Theme List */}
            <div className="mb-6">
                <h4 className="text-sm font-medium text-gray-300 mb-3">Installed Themes ({themes.length})</h4>
                <div className="space-y-2 max-h-64 overflow-y-auto">
                    {themes.map(theme => (
                        <div 
                            key={theme.id} 
                            className={`bg-gray-700 rounded p-3 flex items-center justify-between border-2 transition-colors ${
                                currentTheme === theme.id && !previewTheme 
                                    ? 'border-purple-500' 
                                    : previewTheme === theme.id 
                                    ? 'border-yellow-500' 
                                    : 'border-transparent'
                            }`}
                        >
                            <div className="flex-1">
                                <div className="flex items-center gap-2">
                                    <span className="font-medium text-white">{theme.name}</span>
                                    {currentTheme === theme.id && !previewTheme && (
                                        <span className="px-2 py-0.5 bg-purple-600 text-white text-xs rounded">Active</span>
                                    )}
                                    {previewTheme === theme.id && (
                                        <span className="px-2 py-0.5 bg-yellow-600 text-white text-xs rounded">Preview</span>
                                    )}
                                    {theme.isDefault && (
                                        <span className="px-2 py-0.5 bg-gray-600 text-gray-300 text-xs rounded">System</span>
                                    )}
                                    {theme.format === 'toml' && (
                                        <span className="px-2 py-0.5 bg-blue-600 text-white text-xs rounded">TOML</span>
                                    )}
                                </div>
                                {theme.description && (
                                    <p className="text-xs text-gray-400 mt-1">{theme.description}</p>
                                )}
                                <div className="flex items-center gap-3 mt-1">
                                    {theme.author && (
                                        <p className="text-xs text-gray-500">by {theme.author}</p>
                                    )}
                                    {theme.version && (
                                        <p className="text-xs text-gray-500">v{theme.version}</p>
                                    )}
                                </div>
                            </div>
                            <div className="flex items-center gap-2">
                                {currentTheme !== theme.id && (
                                    <button
                                        onClick={() => handlePreview(theme.id)}
                                        className="p-2 text-gray-400 hover:text-blue-400 transition-colors"
                                        title={previewTheme === theme.id ? "Stop preview" : "Preview theme"}
                                    >
                                        <Eye className="w-4 h-4" />
                                    </button>
                                )}
                                {currentTheme !== theme.id && (
                                    <button
                                        onClick={() => handleThemeChange(theme.id)}
                                        className="p-2 text-gray-400 hover:text-purple-400 transition-colors"
                                        title="Apply theme"
                                    >
                                        <Check className="w-4 h-4" />
                                    </button>
                                )}
                                {/* Delete button handling for all scenarios */}
                                {!theme.isDefault ? (
                                    // Custom theme - can be deleted if authenticated
                                    isAuthenticated ? (
                                        <button
                                            onClick={() => handleDelete(theme.id, theme.name, theme.isDefault)}
                                            disabled={loading}
                                            className="p-2 text-gray-400 hover:text-red-400 transition-colors disabled:opacity-50"
                                            title="Delete theme"
                                        >
                                            <Trash2 className="w-4 h-4" />
                                        </button>
                                    ) : (
                                        <button
                                            onClick={() => {
                                                setUploadError('Authentication required to delete custom themes');
                                                setTimeout(() => setUploadError(null), 5000);
                                            }}
                                            className="p-2 text-gray-400 hover:text-yellow-400 transition-colors"
                                            title="Authentication required"
                                        >
                                            <Lock className="w-4 h-4" />
                                        </button>
                                    )
                                ) : (
                                    // System theme - cannot be deleted
                                    <button
                                        onClick={() => {
                                            setUploadError(`"${theme.name}" is a system theme and cannot be deleted`);
                                            setTimeout(() => setUploadError(null), 5000);
                                        }}
                                        className="p-2 text-gray-600 hover:text-gray-500 cursor-not-allowed transition-colors"
                                        title="System theme - cannot be deleted"
                                    >
                                        <Trash2 className="w-4 h-4 opacity-30" />
                                    </button>
                                )}
                            </div>
                        </div>
                    ))}
                </div>
            </div>

            {/* Upload Section */}
            {isAuthenticated && (
                <>
                    <div className="mb-4">
                        <h4 className="text-sm font-medium text-gray-300 mb-3">Upload Custom Theme</h4>
                        <div
                            className={`border-2 border-dashed rounded-lg p-8 text-center transition-colors ${
                                dragActive
                                    ? 'border-purple-500 bg-purple-900 bg-opacity-20'
                                    : 'border-gray-600 hover:border-gray-500'
                            }`}
                            onDragEnter={handleDrag}
                            onDragLeave={handleDrag}
                            onDragOver={handleDrag}
                            onDrop={handleDrop}
                        >
                            <Upload className="w-12 h-12 text-gray-500 mx-auto mb-3" />
                            <p className="text-gray-300 mb-2">
                                Drag and drop a theme file here, or click to browse
                            </p>
                            <p className="text-xs text-gray-500 mb-3">
                                TOML or JSON format, max 1MB
                            </p>
                            <input
                                ref={fileInputRef}
                                type="file"
                                accept=".json,.toml"
                                onChange={(e) => e.target.files[0] && handleFile(e.target.files[0])}
                                className="hidden"
                            />
                            <button
                                onClick={() => fileInputRef.current?.click()}
                                disabled={loading}
                                className="px-4 py-2 bg-purple-600 hover:bg-purple-700 disabled:opacity-50 rounded text-white text-sm"
                            >
                                {loading ? (
                                    <span className="flex items-center gap-2">
                                        <Loader className="w-4 h-4 animate-spin" />
                                        Processing...
                                    </span>
                                ) : (
                                    'Browse Files'
                                )}
                            </button>
                        </div>
                    </div>

                    <div className="flex justify-center">
                        <button
                            onClick={downloadSampleTheme}
                            className="flex items-center gap-2 text-sm text-purple-400 hover:text-purple-300"
                        >
                            <Download className="w-4 h-4" />
                            Download Sample TOML Theme
                        </button>
                    </div>
                </>
            )}

            {/* Show message if not authenticated */}
            {!isAuthenticated && (
                <div className="p-4 bg-yellow-900 bg-opacity-30 rounded border border-yellow-700">
                    <p className="text-yellow-400 text-sm">Authentication required to upload or delete custom themes</p>
                </div>
            )}

            {/* Error/Success Messages */}
            {uploadError && (
                <div className="mt-4 p-3 bg-red-900 bg-opacity-30 rounded border border-red-700">
                    <div className="flex items-center justify-between">
                        <span className="text-red-400 text-sm">{uploadError}</span>
                        <button onClick={() => setUploadError(null)}>
                            <X className="w-4 h-4 text-red-400" />
                        </button>
                    </div>
                </div>
            )}

            {uploadSuccess && (
                <div className="mt-4 p-3 bg-green-900 bg-opacity-30 rounded border border-green-700">
                    <div className="flex items-center justify-between">
                        <span className="text-green-400 text-sm">{uploadSuccess}</span>
                        <button onClick={() => setUploadSuccess(null)}>
                            <X className="w-4 h-4 text-green-400" />
                        </button>
                    </div>
                </div>
            )}
        </div>
    );
};

export default ThemeManager;