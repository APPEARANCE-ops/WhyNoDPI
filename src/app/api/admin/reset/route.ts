import { NextRequest, NextResponse } from "next/server";
import { db } from "@/lib/db";
import { readSessionFromRequest } from "@/lib/admin-session";
import { audit, sameOrigin } from "@/lib/request-utils";

export const dynamic = "force-dynamic";

/**
 * Danger zone: wipe all client telemetry (used to clear test/demo data before
 * going live). The audit log is preserved so security history is not lost.
 */
export async function POST(request: NextRequest) {
  const headers = request.headers;

  if (!sameOrigin(headers)) {
    return NextResponse.json({ ok: false, error: "forbidden" }, { status: 403 });
  }

  const session = await readSessionFromRequest(request);
  if (!session) {
    return NextResponse.json({ ok: false, error: "unauthorized" }, { status: 401 });
  }

  await db.client.deleteMany({});
  await db.snapshot.deleteMany({});
  await audit("RESET_DATA", headers);

  return NextResponse.json({ ok: true }, { status: 200 });
}
