import type { Config, DatasourceInfo } from '@/types';

export const resolveDatasources = (
  config: Pick<Config, 'cachePath' | 'logsPath' | 'cacheWritable' | 'logsWritable'> & {
    dataSources?: DatasourceInfo[];
  }
): DatasourceInfo[] => {
  if (config.dataSources && config.dataSources.length > 0) {
    return config.dataSources;
  }

  return [
    {
      name: 'default',
      cachePath: config.cachePath,
      logsPath: config.logsPath,
      cacheWritable: config.cacheWritable,
      logsWritable: config.logsWritable,
      enabled: true,
      layout: 'monolithic',
      nginxReopenAvailable: false
    }
  ];
};
