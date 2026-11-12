"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Eye, EyeOff, ShieldCheck, Loader2, TriangleAlert, Lock } from "lucide-react";

export default function AdminLogin({ configured }: { configured: boolean }) {
  const router = useRouter();
  const [password, setPassword] = useState("");
  const [show, setShow] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [, startTransition] = useTransition();

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!password || busy) return;
    setBusy(true);
    setError(null);
    try {
      const res = await fetch("/api/admin/login", {
        method: "POST",
        headers: { "content-type": "application/json" },
        credentials: "same-origin",
        body: JSON.stringify({ password }),
      });
      const data = (await res.json().catch(() => ({}))) as { ok?: boolean; error?: string };
      if (res.ok && data.ok) {
        startTransition(() => router.refresh());
        return;
      }
      if (res.status === 429) {
        setError("Слишком много попыток. Подождите немного и попробуйте снова.");
      } else if (res.status === 403) {
        setError("Запрос отклонён проверкой источника. Обновите страницу и повторите вход.");
      } else {
        setError("Неверный пароль. Доступ запрещён.");
      }
      setPassword("");
    } catch {
      setError("Сеть недоступна. Проверьте соединение и попробуйте снова.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="relative flex min-h-screen items-center justify-center overflow-hidden bg-zinc-950 p-4 text-zinc-100">
      {/* Decorative background */}
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 bg-[radial-gradient(60%_50%_at_50%_0%,rgba(16,185,129,0.16),transparent_70%)]"
      />
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 bg-[radial-gradient(40%_40%_at_85%_85%,rgba(59,130,246,0.12),transparent_70%)]"
      />

      <div className="relative w-full max-w-md">
        <Card className="border-zinc-800 bg-zinc-900/80 shadow-2xl backdrop-blur">
          <CardHeader className="text-center">
            <div className="mx-auto mb-3 flex h-14 w-14 items-center justify-center rounded-2xl border border-emerald-500/30 bg-emerald-500/10">
              <ShieldCheck className="h-7 w-7 text-emerald-400" aria-hidden="true" />
            </div>
            <CardTitle className="text-2xl font-semibold tracking-tight">
              WhyNoDPI · Админ-панель
            </CardTitle>
            <CardDescription className="text-zinc-400">
              Доступ только по паролю администратора. Пароль хранится на сервере в виде
              scrypt-хеша и никогда не покидает его.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {!configured && (
              <div className="mb-4 flex items-start gap-2 rounded-lg border border-amber-500/40 bg-amber-500/10 p-3 text-sm text-amber-300">
                <TriangleAlert className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
                <span>
                  Пароль не настроен на сервере (переменная ADMIN_PASSWORD_HASH). Вход
                  заблокирован в целях безопасности.
                </span>
              </div>
            )}
            <form onSubmit={submit} className="space-y-4" noValidate>
              <div className="space-y-2">
                <Label htmlFor="admin-password">Пароль администратора</Label>
                <div className="relative">
                  <Lock
                    className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-zinc-500"
                    aria-hidden="true"
                  />
                  <Input
                    id="admin-password"
                    name="password"
                    type={show ? "text" : "password"}
                    inputMode="text"
                    autoComplete="current-password"
                    autoFocus
                    required
                    maxLength={200}
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    placeholder="Введите пароль"
                    className="border-zinc-800 bg-zinc-950 pl-9 pr-10"
                    aria-describedby={error ? "login-error" : undefined}
                  />
                  <button
                    type="button"
                    onClick={() => setShow((v) => !v)}
                    className="absolute right-2 top-1/2 -translate-y-1/2 rounded p-2 text-zinc-500 transition-colors hover:text-zinc-300 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-emerald-500/60"
                    aria-label={show ? "Скрыть пароль" : "Показать пароль"}
                    tabIndex={-1}
                  >
                    {show ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                  </button>
                </div>
              </div>

              {error && (
                <p
                  id="login-error"
                  role="alert"
                  className="rounded-lg border border-red-500/40 bg-red-500/10 p-3 text-sm text-red-300"
                >
                  {error}
                </p>
              )}

              <Button
                type="submit"
                disabled={busy || !configured || password.length === 0}
                className="w-full bg-emerald-600 text-white hover:bg-emerald-500 disabled:opacity-50"
              >
                {busy ? (
                  <>
                    <Loader2 className="mr-2 h-4 w-4 animate-spin" aria-hidden="true" />
                    Проверка…
                  </>
                ) : (
                  "Войти в панель"
                )}
              </Button>
            </form>

            <p className="mt-6 text-center text-xs leading-relaxed text-zinc-500">
              Защита: scrypt-хеш пароля · httpOnly-сессия · блокировка после неудачных
              попыток · аудит входов
            </p>
          </CardContent>
        </Card>

        <p className="mt-4 text-center text-xs text-zinc-600">
          WhyNoDPI telemetry · панель администратора
        </p>
      </div>
    </div>
  );
}
