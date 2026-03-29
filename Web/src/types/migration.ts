export type ImportType = 'develancache' | 'lancache-manager';

export type InputMode = 'auto' | 'browse' | 'manual';

export interface ImportResult {
  message: string;
  totalRecords: number;
  imported: number;
  skipped: number;
  errors: number;
  backupPath?: string;
}

export interface ValidationResult {
  valid: boolean;
  message: string;
  recordCount?: number;
}

export interface FileSystemItem {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number;
  lastModified: string;
  isAccessible: boolean;
}

export interface PostgresConnectionConfig {
  host: string;
  port: number;
  database: string;
  username: string;
  password: string;
}
