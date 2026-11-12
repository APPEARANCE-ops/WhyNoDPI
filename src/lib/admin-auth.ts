import { createHash, randomBytes, scryptSync, timingSafeEqual } from "crypto";

/**
 * Server-side admin authentication.
 *
 * Security properties:
 *  - The admin password is stored ONLY as a scrypt hash in the ADMIN_PASSWORD_HASH
 *    environment variable (format: scrypt:N:r:p:saltHex:hashHex). The plaintext
 *    password never touches the database, the code, or the client.
 *  - Verification is constant-time (timingSafeEqual) to prevent timing attacks.
 *  - Sessions are random 32-byte tokens delivered in an httpOnly cookie. The
 *    database stores only the SHA-256 hash of the token, so a DB leak cannot be
 *    replayed as a valid session.
 *  - Session expiry is enforced server-side; expired rows are pruned.
 */

const SCRYPT_KEYLEN = 64;

export const ADMIN_COOKIE = "whynodpi_admin";

export function getSessionTtlHours(): number {
  const n = Number.parseInt(process.env.SESSION_TTL_HOURS ?? "8", 10);
  return Number.isFinite(n) && n > 0 && n <= 168 ? n : 8;
}

/** Parse and validate the scrypt hash line from the environment. */
function parseStoredHash(): {
  N: number;
  r: number;
  p: number;
  salt: Buffer;
  hash: Buffer;
} | null {
  const raw = process.env.ADMIN_PASSWORD_HASH ?? "";
  const parts = raw.trim().split(":");
  if (parts.length !== 6 || parts[0] !== "scrypt") return null;
  const N = Number.parseInt(parts[1], 10);
  const r = Number.parseInt(parts[2], 10);
  const p = Number.parseInt(parts[3], 10);
  try {
    const salt = Buffer.from(parts[4], "hex");
    const hash = Buffer.from(parts[5], "hex");
    if (
      !Number.isFinite(N) ||
      !Number.isFinite(r) ||
      !Number.isFinite(p) ||
      salt.length < 8 ||
      hash.length !== SCRYPT_KEYLEN
    ) {
      return null;
    }
    return { N, r, p, salt, hash };
  } catch {
    return null;
  }
}

/** Constant-time scrypt verification of a candidate password. */
export function verifyAdminPassword(candidate: string): boolean {
  const stored = parseStoredHash();
  if (!stored) {
    // Misconfigured server: fail closed.
    console.error("ADMIN_PASSWORD_HASH is missing or malformed; login disabled");
    return false;
  }
  const candidateHash = scryptSync(candidate.normalize("NFKC"), stored.salt, SCRYPT_KEYLEN, {
    N: stored.N,
    r: stored.r,
    p: stored.p,
  });
  return timingSafeEqual(candidateHash, stored.hash);
}

export function isPasswordConfigured(): boolean {
  return parseStoredHash() !== null;
}

export function hashToken(token: string): string {
  return createHash("sha256").update(token, "utf8").digest("hex");
}

export function generateSessionToken(): string {
  return randomBytes(32).toString("base64url");
}

export function sessionExpiry(from: Date = new Date()): Date {
  return new Date(from.getTime() + getSessionTtlHours() * 3600_000);
}

/**
 * True when the cookie value should be marked Secure. Behind the HTTPS
 * gateway x-forwarded-proto is https; on plain http (local tests) it stays off
 * so the cookie still works in the sandbox.
 */
export function isSecureRequest(headers: Headers): boolean {
  const proto = headers.get("x-forwarded-proto");
  if (proto) return proto.split(",")[0].trim() === "https";
  return false;
}

export function cookieOptions(headers: Headers) {
  return {
    httpOnly: true as const,
    sameSite: "strict" as const,
    secure: isSecureRequest(headers),
    path: "/",
    maxAge: getSessionTtlHours() * 3600,
  };
}
