import { cookies } from "next/headers";
import { db } from "@/lib/db";
import { ADMIN_COOKIE, hashToken, getSessionTtlHours } from "@/lib/admin-auth";

/**
 * Verify the admin session cookie against the database and apply sliding
 * renewal. Returns null when there is no valid session.
 */
export async function getValidSession(token: string | undefined | null) {
  if (!token || token.length < 20 || token.length > 200) return null;
  const id = hashToken(token);
  const now = new Date();
  const session = await db.adminSession.findUnique({ where: { id } });
  if (!session) return null;
  if (session.expiresAt <= now) {
    await db.adminSession.delete({ where: { id } }).catch(() => undefined);
    return null;
  }
  // Sliding renewal: extend once the session is past half its lifetime.
  const halfLife = (getSessionTtlHours() * 3600_000) / 2;
  const remaining = session.expiresAt.getTime() - now.getTime();
  if (remaining < halfLife) {
    const expiresAt = new Date(now.getTime() + getSessionTtlHours() * 3600_000);
    await db.adminSession.update({ where: { id }, data: { expiresAt, lastUsedAt: now } }).catch(() => undefined);
  } else {
    await db.adminSession.update({ where: { id }, data: { lastUsedAt: now } }).catch(() => undefined);
  }
  return session;
}

/** Server-component helper: read cookie + validate. */
export async function readSessionFromCookies() {
  const store = await cookies();
  return getValidSession(store.get(ADMIN_COOKIE)?.value);
}

/** Route-handler helper: validate from the NextRequest cookie header. */
export async function readSessionFromRequest(request: Request) {
  const header = request.headers.get("cookie") ?? "";
  const match = header
    .split(";")
    .map((c) => c.trim())
    .find((c) => c.startsWith(`${ADMIN_COOKIE}=`));
  const token = match ? decodeURIComponent(match.slice(ADMIN_COOKIE.length + 1)) : undefined;
  return getValidSession(token);
}
