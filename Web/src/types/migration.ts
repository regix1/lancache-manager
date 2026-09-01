export interface ImportResult {
  message: string;
  /** i18n key for {@link message}, interpolated from the counts below. */
  stageKey?: string;
  totalRecords: number;
  imported: number;
  skipped: number;
  errors: number;
}

export interface ValidationResult {
  valid: boolean;
  message: string;
  /** i18n key for {@link message}. Absent when the text is the database driver's own. */
  stageKey?: string;
  recordCount?: number;
}

export interface PostgresConnectionConfig {
  host: string;
  port: number;
  database: string;
  username: string;
  password: string;
}
