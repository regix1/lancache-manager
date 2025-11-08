import React, { useState } from 'react';
import { Loader2, RefreshCw, Trash2, Search, CheckCircle, XCircle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';

interface BuildMappingsResult {
  success: boolean;
  product: string;
  mappingsCreated: number;
  mappingsSkipped: number;
  totalMappings: number;
  error?: string;
}

interface BlizzardProduct {
  productCode: string;
  displayName: string;
  region: string;
  isActive: boolean;
  imageUrl?: string;
}

interface ProductStats {
  product: string;
  gameName: string;
  count: number;
}

interface BlizzardStats {
  totalMappings: number;
  productStats: ProductStats[];
}

interface ProductValidation {
  productCode: string;
  displayName: string;
  region: string;
  isActive: boolean;
}

const BlizzardTest: React.FC = () => {
  const [products, setProducts] = useState<BlizzardProduct[]>([]);
  const [stats, setStats] = useState<BlizzardStats | null>(null);
  const [selectedProduct, setSelectedProduct] = useState('');
  const [buildResult, setBuildResult] = useState<BuildMappingsResult | null>(null);
  const [validationResult, setValidationResult] = useState<ProductValidation | null>(null);
  const [validationProductCode, setValidationProductCode] = useState('');
  const [languageFilter, setLanguageFilter] = useState('');
  const [platformFilter, setPlatformFilter] = useState('');

  const [loading, setLoading] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);

  const setLoadingState = (key: string, value: boolean) => {
    setLoading(prev => ({ ...prev, [key]: value }));
  };

  const handleDiscoverProducts = async (forceRefresh: boolean = false) => {
    setLoadingState('discover', true);
    setError(null);
    try {
      const response = await fetch(
        `/api/blizzard/discover-products?forceRefresh=${forceRefresh}`,
        {
          method: 'POST',
          headers: ApiService.getHeaders()
        }
      );

      if (!response.ok) {
        throw new Error('Failed to discover products');
      }

      const data = await response.json();
      setProducts(data.products || []);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoadingState('discover', false);
    }
  };

  const handleGetProducts = async () => {
    setLoadingState('getProducts', true);
    setError(null);
    try {
      const response = await fetch('/api/blizzard/products', {
        headers: ApiService.getHeaders()
      });

      if (!response.ok) {
        throw new Error('Failed to get products');
      }

      const data = await response.json();
      setProducts(data.products || []);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoadingState('getProducts', false);
    }
  };

  const handleGetStats = async () => {
    setLoadingState('stats', true);
    setError(null);
    try {
      const response = await fetch('/api/blizzard/stats', {
        headers: ApiService.getHeaders()
      });

      if (!response.ok) {
        throw new Error('Failed to get stats');
      }

      const data = await response.json();
      setStats(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoadingState('stats', false);
    }
  };

  const handleBuildMappings = async () => {
    if (!selectedProduct) {
      setError('Please select a product first');
      return;
    }

    setLoadingState('build', true);
    setError(null);
    setBuildResult(null);

    try {
      const response = await fetch('/api/blizzard/build-mappings', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...ApiService.getHeaders()
        },
        body: JSON.stringify({
          product: selectedProduct,
          languageFilter: languageFilter || null,
          platformFilter: platformFilter || null
        })
      });

      if (!response.ok) {
        throw new Error('Failed to build mappings');
      }

      const data = await response.json();
      setBuildResult(data);

      // Refresh stats after building
      await handleGetStats();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoadingState('build', false);
    }
  };

  const handleClearProduct = async (product: string) => {
    if (!confirm(`Are you sure you want to clear all mappings for ${product}?`)) {
      return;
    }

    setLoadingState(`clear-${product}`, true);
    setError(null);

    try {
      const response = await fetch(`/api/blizzard/clear-product/${product}`, {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

      if (!response.ok) {
        throw new Error('Failed to clear product mappings');
      }

      // Refresh stats after clearing
      await handleGetStats();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoadingState(`clear-${product}`, false);
    }
  };

  const handleValidateProduct = async () => {
    if (!validationProductCode) {
      setError('Please enter a product code');
      return;
    }

    setLoadingState('validate', true);
    setError(null);
    setValidationResult(null);

    try {
      const response = await fetch(`/api/blizzard/validate-product/${validationProductCode}`, {
        headers: ApiService.getHeaders()
      });

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.error || 'Product validation failed');
      }

      const data = await response.json();
      setValidationResult(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoadingState('validate', false);
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <h1 className="text-3xl font-bold text-themed-primary">Blizzard Test Panel</h1>
      </div>

      {error && (
        <div className="bg-red-500/10 border border-red-500 text-red-500 px-4 py-3 rounded">
          {error}
        </div>
      )}

      {/* Product Discovery Section */}
      <Card>
        <div className="p-6">
          <h2 className="text-xl font-semibold mb-4 text-themed-primary">Product Discovery</h2>
          <div className="flex flex-wrap gap-3">
            <Button
              onClick={() => handleDiscoverProducts(false)}
              disabled={loading.discover}
              className="flex items-center gap-2"
            >
              {loading.discover ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Search className="w-4 h-4" />
              )}
              Discover Products (Cached)
            </Button>
            <Button
              onClick={() => handleDiscoverProducts(true)}
              disabled={loading.discover}
              className="flex items-center gap-2"
            >
              {loading.discover ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <RefreshCw className="w-4 h-4" />
              )}
              Force Refresh
            </Button>
            <Button
              onClick={handleGetProducts}
              disabled={loading.getProducts}
              className="flex items-center gap-2"
            >
              {loading.getProducts ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Search className="w-4 h-4" />
              )}
              Get Cached Products
            </Button>
          </div>

          {products.length > 0 && (
            <div className="mt-4">
              <h3 className="text-lg font-medium mb-2 text-themed-primary">
                Discovered Products ({products.length})
              </h3>
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3 max-h-96 overflow-y-auto">
                {products.map(product => (
                  <div
                    key={product.productCode}
                    className="p-3 border rounded-lg"
                    style={{
                      borderColor: product.isActive ? 'var(--theme-border)' : 'var(--theme-border-muted)',
                      backgroundColor: 'var(--theme-bg-secondary)',
                      opacity: product.isActive ? 1 : 0.5
                    }}
                  >
                    <div className="flex items-start justify-between">
                      <div>
                        <p className="font-mono text-sm font-bold text-themed-primary">
                          {product.productCode}
                        </p>
                        <p className="text-sm text-themed-secondary">{product.displayName}</p>
                        <p className="text-xs text-themed-muted">Region: {product.region}</p>
                      </div>
                      {product.isActive ? (
                        <CheckCircle className="w-5 h-5 text-green-500 flex-shrink-0" />
                      ) : (
                        <XCircle className="w-5 h-5 text-red-500 flex-shrink-0" />
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      </Card>

      {/* Product Validation Section */}
      <Card>
        <div className="p-6">
          <h2 className="text-xl font-semibold mb-4 text-themed-primary">Product Validation</h2>
          <div className="flex gap-3 items-end">
            <div className="flex-1">
              <label className="block text-sm font-medium mb-2 text-themed-secondary">
                Product Code
              </label>
              <input
                type="text"
                value={validationProductCode}
                onChange={(e) => setValidationProductCode(e.target.value.toLowerCase())}
                placeholder="e.g., wow, d3, hero"
                className="w-full px-3 py-2 border rounded-lg"
                style={{
                  backgroundColor: 'var(--theme-bg-secondary)',
                  borderColor: 'var(--theme-border)',
                  color: 'var(--theme-text-primary)'
                }}
              />
            </div>
            <Button
              onClick={handleValidateProduct}
              disabled={loading.validate}
              className="flex items-center gap-2"
            >
              {loading.validate ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <Search className="w-4 h-4" />
              )}
              Validate
            </Button>
          </div>

          {validationResult && (
            <div className="mt-4 p-4 border rounded-lg" style={{
              borderColor: 'var(--theme-border)',
              backgroundColor: 'var(--theme-bg-secondary)'
            }}>
              <div className="flex items-start justify-between">
                <div>
                  <p className="font-mono text-lg font-bold text-themed-primary">
                    {validationResult.productCode}
                  </p>
                  <p className="text-themed-secondary">{validationResult.displayName}</p>
                  <p className="text-sm text-themed-muted">Region: {validationResult.region}</p>
                </div>
                {validationResult.isActive ? (
                  <div className="flex items-center gap-2 text-green-500">
                    <CheckCircle className="w-6 h-6" />
                    <span className="font-medium">Active</span>
                  </div>
                ) : (
                  <div className="flex items-center gap-2 text-red-500">
                    <XCircle className="w-6 h-6" />
                    <span className="font-medium">Inactive</span>
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      </Card>

      {/* Build Mappings Section */}
      <Card>
        <div className="p-6">
          <h2 className="text-xl font-semibold mb-4 text-themed-primary">Build Chunk Mappings</h2>
          <div className="space-y-3">
            <div className="flex gap-3 items-end">
              <div className="flex-1">
                <label className="block text-sm font-medium mb-2 text-themed-secondary">
                  Select Product
                </label>
                <select
                  value={selectedProduct}
                  onChange={(e) => setSelectedProduct(e.target.value)}
                  className="w-full px-3 py-2 border rounded-lg"
                  style={{
                    backgroundColor: 'var(--theme-bg-secondary)',
                    borderColor: 'var(--theme-border)',
                    color: 'var(--theme-text-primary)'
                  }}
                >
                  <option value="">Select a product...</option>
                  {products
                    .filter(p => p.isActive)
                    .map(product => (
                      <option key={product.productCode} value={product.productCode}>
                        {product.displayName} ({product.productCode})
                      </option>
                    ))}
                </select>
              </div>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              <div>
                <label className="block text-sm font-medium mb-2 text-themed-secondary">
                  Language Filter (optional)
                </label>
                <input
                  type="text"
                  value={languageFilter}
                  onChange={(e) => setLanguageFilter(e.target.value)}
                  placeholder="e.g., enUS, frFR, deDE (leave empty for all)"
                  className="w-full px-3 py-2 border rounded-lg text-sm"
                  style={{
                    backgroundColor: 'var(--theme-bg-secondary)',
                    borderColor: 'var(--theme-border)',
                    color: 'var(--theme-text-primary)'
                  }}
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-2 text-themed-secondary">
                  Platform Filter (optional)
                </label>
                <input
                  type="text"
                  value={platformFilter}
                  onChange={(e) => setPlatformFilter(e.target.value)}
                  placeholder="e.g., Windows, Mac (leave empty for all)"
                  className="w-full px-3 py-2 border rounded-lg text-sm"
                  style={{
                    backgroundColor: 'var(--theme-bg-secondary)',
                    borderColor: 'var(--theme-border)',
                    color: 'var(--theme-text-primary)'
                  }}
                />
              </div>
            </div>

            <Button
              onClick={handleBuildMappings}
              disabled={loading.build || !selectedProduct}
              className="flex items-center gap-2"
            >
              {loading.build ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <RefreshCw className="w-4 h-4" />
              )}
              Build Mappings
            </Button>
          </div>

          {buildResult && (
            <div className={`mt-4 p-4 border rounded-lg ${
              buildResult.success
                ? 'bg-green-500/10 border-green-500'
                : 'bg-red-500/10 border-red-500'
            }`}>
              {buildResult.success ? (
                <div className="space-y-2">
                  <p className="font-semibold text-green-500">Build Successful!</p>
                  <p className="text-themed-secondary">Product: {buildResult.product}</p>
                  <p className="text-themed-secondary">Mappings Created: {buildResult.mappingsCreated}</p>
                  <p className="text-themed-secondary">Mappings Skipped: {buildResult.mappingsSkipped}</p>
                  <p className="text-themed-secondary">Total Mappings: {buildResult.totalMappings}</p>
                </div>
              ) : (
                <div>
                  <p className="font-semibold text-red-500">Build Failed</p>
                  <p className="text-red-500">{buildResult.error}</p>
                </div>
              )}
            </div>
          )}
        </div>
      </Card>

      {/* Statistics Section */}
      <Card>
        <div className="p-6">
          <div className="flex justify-between items-center mb-4">
            <h2 className="text-xl font-semibold text-themed-primary">Mapping Statistics</h2>
            <Button
              onClick={handleGetStats}
              disabled={loading.stats}
              className="flex items-center gap-2"
            >
              {loading.stats ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : (
                <RefreshCw className="w-4 h-4" />
              )}
              Refresh Stats
            </Button>
          </div>

          {stats && (
            <div className="space-y-4">
              <div className="p-4 rounded-lg" style={{
                backgroundColor: 'var(--theme-bg-secondary)',
                borderLeft: '4px solid var(--theme-accent)'
              }}>
                <p className="text-2xl font-bold text-themed-primary">
                  {stats.totalMappings.toLocaleString()}
                </p>
                <p className="text-themed-secondary">Total Chunk Mappings</p>
              </div>

              {stats.productStats.length > 0 && (
                <div>
                  <h3 className="text-lg font-medium mb-3 text-themed-primary">By Product</h3>
                  <div className="space-y-2">
                    {stats.productStats.map(productStat => (
                      <div
                        key={productStat.product}
                        className="flex items-center justify-between p-3 border rounded-lg"
                        style={{
                          borderColor: 'var(--theme-border)',
                          backgroundColor: 'var(--theme-bg-secondary)'
                        }}
                      >
                        <div>
                          <p className="font-medium text-themed-primary">{productStat.gameName}</p>
                          <p className="text-sm font-mono text-themed-muted">{productStat.product}</p>
                        </div>
                        <div className="flex items-center gap-3">
                          <p className="font-semibold text-themed-primary">
                            {productStat.count.toLocaleString()} mappings
                          </p>
                          <Button
                            onClick={() => handleClearProduct(productStat.product)}
                            disabled={loading[`clear-${productStat.product}`]}
                            variant="danger"
                            className="flex items-center gap-2"
                          >
                            {loading[`clear-${productStat.product}`] ? (
                              <Loader2 className="w-4 h-4 animate-spin" />
                            ) : (
                              <Trash2 className="w-4 h-4" />
                            )}
                            Clear
                          </Button>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      </Card>
    </div>
  );
};

export default BlizzardTest;
