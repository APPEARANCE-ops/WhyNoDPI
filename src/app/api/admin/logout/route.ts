import { NextRequest, NextResponse } from "next/server";
import { db } from "@/lib/db";
import { ADMIN_COOKIE, hashToken } from "@/lib/admin-auth";
import { readSessionFromRequest } from "@/lib/admin-session";
import { audit, sameOrigin } from "@/lib/request-utils";

export const dynamic = "force-dynamic";

export async function POST(request: NextRequest) {
  const headers = request.headers;

  if (!sameOrigin(headers)) {
    return NextResponse.json({ ok: false, error: "forbidden" }, { status: 403 });
  }

  const session = await readSessionFromRequest(request);
  if (session) {
    await db.adminSession.delete({ where: { id: session.id } }).catch(() => undefined);
    await audit("LOGOUT", headers);
  }

  const response = NextResponse.json({ ok: true }, { status: 200 });
  response.cookies.set(ADMIN_COOKIE, "", {
    httpOnly: true,
    sameSite: "strict",
    path: "/",
    maxAge: 0,
  });
  return response;
}
