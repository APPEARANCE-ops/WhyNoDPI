/**
 * Minimal in-memory sliding-window rate limiter with failure lockout.
 * Suitable for a single-node deployment; no external services required.
 */

type Bucket = {
  hits: number[]; // timestamps (ms) inside the current window
  lockedUntil: number;
  fails: number;
};

const buckets = new Map<string, Bucket>();

// Periodic cleanup so the map cannot grow unbounded.
let lastSweep = Date.now();
function sweep(now: number) {
  if (now - lastSweep < 60_000) return;
  lastSweep = now;
  for (const [key, b] of buckets) {
    if (b.lockedUntil < now && (b.hits.length === 0 || now - b.hits[b.hits.length - 1] > 3_600_000)) {
      buckets.delete(key);
    }
  }
}

export type LimitResult = {
  allowed: boolean;
  retryAfterSeconds: number;
  remaining: number;
};

export function rateLimit(
  key: string,
  opts: { windowMs: number; max: number; lockoutMs?: number; lockAfter?: number }
): LimitResult {
  const now = Date.now();
  sweep(now);

  let bucket = buckets.get(key);
  if (!bucket) {
    bucket = { hits: [], lockedUntil: 0, fails: 0 };
    buckets.set(key, bucket);
  }

  if (bucket.lockedUntil > now) {
    return {
      allowed: false,
      retryAfterSeconds: Math.ceil((bucket.lockedUntil - now) / 1000),
      remaining: 0,
    };
  }

  bucket.hits = bucket.hits.filter((t) => now - t < opts.windowMs);

  if (bucket.hits.length >= opts.max) {
    if (opts.lockAfter != null && opts.lockoutMs) {
      const recentFails = bucket.hits.length;
      if (recentFails >= opts.lockAfter) {
        bucket.lockedUntil = now + opts.lockoutMs;
        bucket.fails += 1;
      }
    }
    return { allowed: false, retryAfterSeconds: Math.ceil(opts.windowMs / 1000), remaining: 0 };
  }

  bucket.hits.push(now);
  return { allowed: true, retryAfterSeconds: 0, remaining: opts.max - bucket.hits.length };
}

export function recordFailure(key: string, opts: { lockoutMs: number; lockAfter: number }): void {
  const now = Date.now();
  const bucket = buckets.get(key) ?? { hits: [], lockedUntil: 0, fails: 0 };
  bucket.fails += 1;
  if (bucket.fails >= opts.lockAfter) {
    bucket.lockedUntil = now + opts.lockoutMs;
    bucket.fails = 0;
    bucket.hits = [];
  }
  buckets.set(key, bucket);
}

export function resetFailures(key: string): void {
  const bucket = buckets.get(key);
  if (bucket) {
    bucket.fails = 0;
    bucket.lockedUntil = 0;
  }
}

/** Combined limiter+lockout policy for admin login. */
export const LOGIN_LIMIT = { windowMs: 10 * 60_000, max: 10 };
export const LOGIN_LOCK = { lockoutMs: 15 * 60_000, lockAfter: 5 };
export const HEARTBEAT_LIMIT = { windowMs: 60_000, max: 30 };
export const ADMIN_API_LIMIT = { windowMs: 60_000, max: 120 };
