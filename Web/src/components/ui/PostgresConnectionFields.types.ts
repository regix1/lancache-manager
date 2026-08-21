export interface PostgresConnectionValues {
  host: string;
  port: string;
  database: string;
  username: string;
  password: string;
}

export type PostgresConnectionField = keyof PostgresConnectionValues;

/** Already-translated label for each field. The two callers use different i18n namespaces. */
export type PostgresConnectionLabels = Record<PostgresConnectionField, string>;
