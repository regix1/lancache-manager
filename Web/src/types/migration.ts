export interface ImportResult {
  /** English text for logs and older callers; the browser shows {@link stageKey}. */
  message: string;
  /** i18n key for the headline, interpolated from the counts below. */
  stageKey: string;
  totalRecords: number;
  imported: number;
  skipped: number;
  errors: number;
}

export interface ValidationResult {
  valid: boolean;
  /** English text for logs; on a failure it is the driver's own text, shown as `{{detail}}`. */
  message: string;
  /** i18n key for the shown sentence, interpolated with `detail` from {@link message}. */
  stageKey: string;
  recordCount?: number;
}

export interface PostgresConnectionConfig {
  host: string;
  port: number;
  database: string;
  username: string;
  password: string;
}
