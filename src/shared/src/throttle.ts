export interface ThrottledFn<T> {
  (value: T): void;
  /** Fire any pending call immediately and cancel the timer. */
  flush: () => void;
  /** Drop any pending call. */
  cancel: () => void;
}

/**
 * Leading + trailing throttle. Calls fire at most once every `ms` milliseconds.
 * If multiple values arrive in a window, the LAST value gets fired at the end
 * of the window (so the receiver sees the final value the user landed on, not
 * an intermediate one).
 *
 * Designed for slider drags hitting a host endpoint: limit network chatter
 * during a continuous drag while still applying the final release value.
 */
export function throttle<T>(fn: (value: T) => void, ms: number): ThrottledFn<T> {
  let lastFire = 0;
  let pending: { value: T } | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;

  const fire = (): void => {
    if (pending !== null) {
      const v = pending.value;
      pending = null;
      lastFire = Date.now();
      fn(v);
    }
  };

  const throttled = ((value: T): void => {
    pending = { value };
    const since = Date.now() - lastFire;
    if (since >= ms) {
      if (timer !== null) {
        clearTimeout(timer);
        timer = null;
      }
      fire();
    } else if (timer === null) {
      timer = setTimeout(() => {
        timer = null;
        fire();
      }, ms - since);
    }
  }) as ThrottledFn<T>;

  throttled.flush = (): void => {
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
    fire();
  };

  throttled.cancel = (): void => {
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
    pending = null;
  };

  return throttled;
}
