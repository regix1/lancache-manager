import ApiService from '@services/api.service';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';

const sleep = (ms: number): Promise<void> =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

interface WaitForPersistentContainerAuthOptions {
  maxAttempts?: number;
  intervalMs?: number;
}

/**
 * Polls the persistent-container list until the daemon reports authenticated,
 * the container stops, or attempts are exhausted. Used after starting a container
 * so we do not prompt for login while the daemon is still self-authenticating
 * from its named auth volume.
 */
export async function waitForPersistentContainerAuth(
  serviceId: PersistentPrefillServiceId,
  options: WaitForPersistentContainerAuthOptions = {}
): Promise<{
  containers: PersistentPrefillContainerDto[];
  container: PersistentPrefillContainerDto | undefined;
}> {
  const maxAttempts = options.maxAttempts ?? 12;
  const intervalMs = options.intervalMs ?? 1000;

  let containers: PersistentPrefillContainerDto[] = [];

  for (let attempt = 0; attempt < maxAttempts; attempt++) {
    containers = await ApiService.getPersistentPrefillContainers();
    const container = containers.find((item) => item.service === serviceId);

    if (!container?.isRunning || container.isAuthenticated) {
      return { containers, container };
    }

    if (attempt < maxAttempts - 1) {
      await sleep(intervalMs);
    }
  }

  containers = await ApiService.getPersistentPrefillContainers();
  const container = containers.find((item) => item.service === serviceId);
  return { containers, container };
}
