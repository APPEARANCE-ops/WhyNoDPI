import { createHash } from "crypto";
import { db } from "@/lib/db";
import { hashToken } from "@/lib/admin-auth";

/** Extract the client IP from proxy headers (best effort, no logging of raw IP). */
export function getClientIp(headers: Headers): string {
  const xff = headers.get("x-forwarded-for");
  if (xff) return xff.split(",")[0].trim();
  return headers.get("x-real-ip")?.trim() || "unknown";
}

/**
 * IPs are stored only as salted hashes in the audit log (privacy by design).
 * The pepper is the same server secret used for install IDs.
 */
export function hashIp(ip: string): string {
  const pepper = process.env.TELEMETRY_PEPPER ?? "";
  return createHash("sha256").update(`${pepper}:${ip}`, "utf8").digest("hex").slice(0, 32);
}

/** One-way hash of an anonymous install GUID, used as the client record key. */
export function hashInstallId(installId: string): string {
  const pepper = process.env.TELEMETRY_PEPPER ?? "";
  return createHash("sha256").update(`${pepper}:install:${installId}`, "utf8").digest("hex");
}

export async function audit(
  event: "LOGIN_OK" | "LOGIN_FAIL" | "LOGOUT" | "DENIED" | "RESET_DATA" | "RATE_LIMITED" | "HEARTBEAT_REJECTED",
  headers: Headers,
  detail = ""
): Promise<void> {
  try {
    await db.auditLog.create({
      data: {
        event,
        ipHash: hashIp(getClientIp(headers)),
        detail: detail.slice(0, 300),
      },
    });
  } catch (e) {
    // Audit must never break the request path.
    console.error("audit write failed", e);
  }
}

/**
 * CSRF hardening for state-changing admin endpoints: the browser always sends
 * Origin (or Referer) on cross-site POSTs; the desktop heartbeat client never
 * calls these endpoints. Strict Same-Site cookies are the first line, this is
 * the second.
 */
export function sameOrigin(headers: Headers): boolean {
  const host = headers.get("x-forwarded-host") ?? headers.get("host");
  if (!host) return false;
  const origin = headers.get("origin");
  if (origin) {
    try {
      return new URL(origin).host === host;
    } catch {
      return false;
    }
  }
  const referer = headers.get("referer");
  if (referer) {
    try {
      return new URL(referer).host === host;
    } catch {
      return false;
    }
  }
  // No Origin and no Referer: allow only non-browser clients for GET,
  // block for POST to be safe.
  return false;
}

/** Session cookie name re-export for convenience in routes. */
export { ADMIN_COOKIE, hashToken } from "@/lib/admin-auth";
