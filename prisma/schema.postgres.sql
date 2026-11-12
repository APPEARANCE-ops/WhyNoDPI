-- Таблицы панели WhyNoDPI для Postgres (Vercel / Neon).
--
-- Вариант без Node.js: вставьте этот файл целиком в Neon → SQL Editor → Run.
-- Вариант с Node.js: npx prisma db push --schema prisma/schema.postgres.prisma
--
-- Имена таблиц, колонок, значений по умолчанию и индексов совпадают с
-- prisma/schema.postgres.prisma, поэтому Prisma считает такую базу «в синхроне».

CREATE TABLE IF NOT EXISTS "Client" (
    "id"          TEXT           NOT NULL,
    "firstSeenAt" TIMESTAMP(3)   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "lastSeenAt"  TIMESTAMP(3)   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "stoppedAt"   TIMESTAMP(3),
    "appVersion"  TEXT           NOT NULL DEFAULT 'unknown',
    "osInfo"      TEXT           NOT NULL DEFAULT 'windows',
    "strategy"    TEXT           NOT NULL DEFAULT '-',
    CONSTRAINT "Client_pkey" PRIMARY KEY ("id")
);

CREATE INDEX IF NOT EXISTS "Client_lastSeenAt_idx"  ON "Client" ("lastSeenAt");
CREATE INDEX IF NOT EXISTS "Client_firstSeenAt_idx" ON "Client" ("firstSeenAt");

CREATE TABLE IF NOT EXISTS "AdminSession" (
    "id"         TEXT         NOT NULL,
    "createdAt"  TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "lastUsedAt" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "expiresAt"  TIMESTAMP(3) NOT NULL,
    "userAgent"  TEXT         NOT NULL DEFAULT '',
    CONSTRAINT "AdminSession_pkey" PRIMARY KEY ("id")
);

CREATE INDEX IF NOT EXISTS "AdminSession_expiresAt_idx" ON "AdminSession" ("expiresAt");

CREATE TABLE IF NOT EXISTS "Snapshot" (
    "minute"      INTEGER      NOT NULL,
    "onlineCount" INTEGER      NOT NULL DEFAULT 0,
    "recordedAt"  TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "Snapshot_pkey" PRIMARY KEY ("minute")
);

CREATE INDEX IF NOT EXISTS "Snapshot_minute_idx" ON "Snapshot" ("minute");

CREATE TABLE IF NOT EXISTS "AuditLog" (
    "id"        SERIAL       NOT NULL,
    "event"     TEXT         NOT NULL,
    "ipHash"    TEXT         NOT NULL DEFAULT '',
    "detail"    TEXT         NOT NULL DEFAULT '',
    "createdAt" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "AuditLog_pkey" PRIMARY KEY ("id")
);

CREATE INDEX IF NOT EXISTS "AuditLog_event_idx"  ON "AuditLog" ("event");
CREATE INDEX IF NOT EXISTS "AuditLog_createdAt_idx" ON "AuditLog" ("createdAt");
CREATE INDEX IF NOT EXISTS "AuditLog_ipHash_event_createdAt_idx" ON "AuditLog" ("ipHash", "event", "createdAt");
