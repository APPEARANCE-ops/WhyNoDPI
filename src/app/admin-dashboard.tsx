"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import {
  Area,
  AreaChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import {
  Activity,
  Users,
  UserPlus,
  Clock3,
  RefreshCw,
  LogOut,
  Trash2,
  ShieldCheck,
  Loader2,
  MonitorSmartphone,
  Search,
  Download,
  Layers,
  CheckCircle2,
  XCircle,
} from "lucide-react";

type Stats = {
  serverTime: string;
  onlineWindowSeconds: number;
  online: number;
  total: number;
  newToday: number;
  active24h: number;
  versions: { name: string; count: number }[];
  oses: { name: string; count: number }[];
  strategies: { name: string; count: number }[];
  recent: {
    idMasked: string;
    firstSeenAt: string;
    lastSeenAt: string;
    online: boolean;
    appVersion: string;
    osInfo: string;
    strategy: string;
  }[];
  trend: { t: number; online: number }[];
};

type ClientRow = Stats["recent"][number];
type OnlineFilter = "all" | "online" | "offline";

const REFRESH_MS = 10_000;

function fmtRelative(iso: string, nowMs: number): string {
  const diff = Math.max(0, nowMs - new Date(iso).getTime());
  const s = Math.floor(diff / 1000);
  if (s < 15) return "только что";
  if (s < 60) return `${s} с назад`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} мин назад`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} ч ${m % 60} мин назад`;
  const d = Math.floor(h / 24);
  return `${d} дн назад`;
}

function fmtClock(iso: string): string {
  return new Date(iso).toLocaleTimeString("ru-RU", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

function fmtDateTime(iso: string): string {
  return new Date(iso).toLocaleString("ru-RU", {
    day: "2-digit",
    month: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function BreakdownList({
  items,
  emptyText,
}: {
  items: { name: string; count: number }[];
  emptyText: string;
}) {
  if (items.length === 0) {
    return <p className="text-sm text-zinc-500">{emptyText}</p>;
  }
  const max = Math.max(...items.map((i) => i.count), 1);
  return (
    <ul className="space-y-2.5">
      {items.map((item) => (
        <li key={item.name}>
          <div className="flex items-center justify-between gap-2 text-sm">
            <span className="truncate text-zinc-300" title={item.name}>
              {item.name}
            </span>
            <span className="shrink-0 tabular-nums text-zinc-400">{item.count}</span>
          </div>
          <div className="mt-1.5 h-1.5 w-full overflow-hidden rounded-full bg-zinc-800">
            <div
              className="h-full rounded-full bg-emerald-500/70"
              style={{ width: `${Math.max(4, (item.count / max) * 100)}%` }}
            />
          </div>
        </li>
      ))}
    </ul>
  );
}

function StatSkeleton() {
  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
      {[0, 1, 2, 3].map((i) => (
        <Card key={i} className="border-zinc-800 bg-zinc-900/80">
          <CardHeader className="pb-2">
            <Skeleton className="h-3.5 w-28 bg-zinc-800" />
            <Skeleton className="mt-3 h-9 w-20 bg-zinc-800" />
          </CardHeader>
          <CardContent>
            <Skeleton className="h-3 w-40 bg-zinc-800" />
          </CardContent>
        </Card>
      ))}
    </div>
  );
}

export default function AdminDashboard() {
  const router = useRouter();
  const [stats, setStats] = useState<Stats | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [auto, setAuto] = useState(true);
  const [lastUpdate, setLastUpdate] = useState<number | null>(null);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [resetting, setResetting] = useState(false);
  const [confirmText, setConfirmText] = useState("");
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [resetMessage, setResetMessage] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [onlineFilter, setOnlineFilter] = useState<OnlineFilter>("all");
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const load = useCallback(
    async (options?: { silent?: boolean }) => {
      if (!options?.silent) setRefreshing(true);
      try {
        const res = await fetch("/api/admin/stats", {
          credentials: "same-origin",
          cache: "no-store",
        });
        if (res.status === 401) {
          router.refresh();
          return;
        }
        if (res.status === 429) {
          setError("Слишком много запросов к статистике. Подождите минуту.");
          return;
        }
        const data = (await res.json().catch(() => ({}))) as { ok?: boolean; stats?: Stats };
        if (res.ok && data.ok && data.stats) {
          if (!mountedRef.current) return;
          setStats(data.stats);
          setError(null);
          setLastUpdate(Date.now());
        } else {
          setError("Не удалось получить статистику.");
        }
      } catch {
        setError("Сеть недоступна. Пробуем снова…");
      } finally {
        if (mountedRef.current) {
          setLoading(false);
          setRefreshing(false);
        }
      }
    },
    [router]
  );

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    if (!auto) return;
    const id = setInterval(() => {
      if (document.visibilityState === "visible") load({ silent: true });
    }, REFRESH_MS);
    return () => clearInterval(id);
  }, [auto, load]);

  // Relative timestamps only need a coarse tick; once a second re-rendered the
  // whole table constantly.
  useEffect(() => {
    const id = setInterval(() => setNowMs(Date.now()), 5_000);
    return () => clearInterval(id);
  }, []);

  async function logout() {
    await fetch("/api/admin/logout", {
      method: "POST",
      credentials: "same-origin",
    }).catch(() => undefined);
    router.refresh();
  }

  async function resetData() {
    setResetting(true);
    setResetMessage(null);
    try {
      const res = await fetch("/api/admin/reset", {
        method: "POST",
        credentials: "same-origin",
      });
      if (!res.ok) {
        setResetMessage("Не удалось очистить данные. Обновите страницу и попробуйте снова.");
        return;
      }
      setConfirmOpen(false);
      setConfirmText("");
      setResetMessage("Телеметрия очищена. Сбор данных начат заново.");
      await load();
    } catch {
      setResetMessage("Сеть недоступна. Данные не изменены.");
    } finally {
      setResetting(false);
    }
  }

  const clients = useMemo<ClientRow[]>(() => {
    const rows = stats?.recent ?? [];
    const needle = query.trim().toLowerCase();
    return rows.filter((row) => {
      if (onlineFilter === "online" && !row.online) return false;
      if (onlineFilter === "offline" && row.online) return false;
      if (!needle) return true;
      return (
        row.idMasked.toLowerCase().includes(needle) ||
        row.appVersion.toLowerCase().includes(needle) ||
        row.osInfo.toLowerCase().includes(needle) ||
        row.strategy.toLowerCase().includes(needle)
      );
    });
  }, [stats, query, onlineFilter]);

  function exportCsv() {
    const header = [
      "Клиент",
      "Версия",
      "ОС",
      "Стратегия",
      "Статус",
      "Первое подключение",
      "Последняя активность",
    ];
    const lines = clients.map((c) => [
      c.idMasked,
      c.appVersion,
      c.osInfo,
      c.strategy,
      c.online ? "в сети" : "офлайн",
      fmtDateTime(c.firstSeenAt),
      fmtDateTime(c.lastSeenAt),
    ]);
    const csv = [header, ...lines]
      .map((row) => row.map((cell) => `"${String(cell).replace(/"/g, '""')}"`).join(","))
      .join("\r\n");
    const blob = new Blob(["\uFEFF" + csv], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = `whynodpi-clients-${new Date().toISOString().slice(0, 10)}.csv`;
    anchor.click();
    URL.revokeObjectURL(url);
  }

  const trendData = (stats?.trend ?? []).map((p) => ({
    ...p,
    label: new Date(p.t).toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" }),
  }));

  const onlineTotal = stats?.online ?? 0;
  const updatedAgo = lastUpdate ? fmtRelative(new Date(lastUpdate).toISOString(), nowMs) : "—";

  return (
    <div className="flex min-h-screen flex-col bg-zinc-950 text-zinc-100">
      {/* Header */}
      <header className="sticky top-0 z-20 border-b border-zinc-800 bg-zinc-950/90 backdrop-blur supports-[backdrop-filter]:bg-zinc-950/75">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-3 px-4 py-3 sm:px-6">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-xl border border-emerald-500/30 bg-emerald-500/10">
              <ShieldCheck className="h-5 w-5 text-emerald-400" aria-hidden="true" />
            </div>
            <div>
              <h1 className="text-sm font-semibold leading-tight sm:text-base">
                WhyNoDPI · Админ-панель
              </h1>
              <p className="flex flex-wrap items-center gap-x-2 text-xs leading-tight text-zinc-500">
                <span className="inline-flex items-center gap-1.5">
                  <span
                    className={`inline-block h-1.5 w-1.5 rounded-full ${
                      error ? "bg-amber-400" : "bg-emerald-500"
                    }`}
                    aria-hidden="true"
                  />
                  {updatedAgo}
                </span>
                <span aria-hidden="true">·</span>
                <span className="tabular-nums">
                  сервер: {stats ? fmtClock(stats.serverTime) : "—"}
                </span>
              </p>
            </div>
          </div>

          <div className="ml-auto flex flex-wrap items-center gap-3">
            <div className="flex items-center gap-2">
              <Switch
                id="auto-refresh"
                checked={auto}
                onCheckedChange={setAuto}
                aria-label="Автообновление каждые 10 секунд"
              />
              <Label htmlFor="auto-refresh" className="text-xs text-zinc-400">
                Авто 10 с
              </Label>
            </div>
            <Button
              variant="outline"
              size="sm"
              onClick={() => load()}
              disabled={refreshing}
              className="border-zinc-800 bg-zinc-900 hover:bg-zinc-800 hover:text-zinc-100"
              aria-label="Обновить статистику"
            >
              <RefreshCw
                className={`h-4 w-4 ${refreshing ? "animate-spin" : ""}`}
                aria-hidden="true"
              />
              <span className="ml-1.5 hidden sm:inline">Обновить</span>
            </Button>
            <Button
              variant="outline"
              size="sm"
              onClick={logout}
              className="border-zinc-800 bg-zinc-900 hover:bg-zinc-800 hover:text-zinc-100"
              aria-label="Выйти из админ-панели"
            >
              <LogOut className="h-4 w-4" aria-hidden="true" />
              <span className="ml-1.5 hidden sm:inline">Выйти</span>
            </Button>
          </div>
        </div>
      </header>

      <main className="mx-auto w-full max-w-6xl flex-1 px-4 py-6 sm:px-6">
        {loading && (
          <div className="space-y-6" role="status" aria-label="Загрузка статистики">
            <StatSkeleton />
            <Card className="border-zinc-800 bg-zinc-900/80">
              <CardContent className="p-6">
                <Skeleton className="h-64 w-full bg-zinc-800" />
              </CardContent>
            </Card>
          </div>
        )}

        {!loading && error && !stats && (
          <Card className="border-red-900/50 bg-red-950/20">
            <CardContent className="flex flex-wrap items-center gap-3 p-6 text-sm text-red-200">
              <XCircle className="h-5 w-5 text-red-400" aria-hidden="true" />
              <span role="alert">{error}</span>
              <Button
                size="sm"
                variant="outline"
                onClick={() => load()}
                className="ml-auto border-red-900/60 bg-red-950/40 text-red-200 hover:bg-red-900/40"
              >
                Повторить
              </Button>
            </CardContent>
          </Card>
        )}

        {!loading && stats && (
          <div className="space-y-6">
            {error && (
              <p
                role="alert"
                className="rounded-lg border border-amber-500/40 bg-amber-500/10 p-3 text-sm text-amber-300"
              >
                {error} Показаны последние полученные данные.
              </p>
            )}

            {/* Key metrics */}
            <section
              aria-label="Ключевые показатели"
              className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4"
            >
              <Card className="border-emerald-800/40 bg-gradient-to-br from-emerald-950/60 to-zinc-900">
                <CardHeader className="pb-2">
                  <CardDescription className="flex items-center gap-2 text-emerald-300/90">
                    <span className="relative flex h-2.5 w-2.5">
                      {onlineTotal > 0 && (
                        <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-60" />
                      )}
                      <span className="relative inline-flex h-2.5 w-2.5 rounded-full bg-emerald-500" />
                    </span>
                    В сети сейчас
                  </CardDescription>
                  <CardTitle
                    aria-live="polite"
                    className="text-4xl font-bold tabular-nums text-emerald-300"
                  >
                    {onlineTotal}
                  </CardTitle>
                </CardHeader>
                <CardContent className="text-xs text-zinc-500">
                  Онлайн = heartbeat за последние {stats.onlineWindowSeconds} с
                </CardContent>
              </Card>

              <Card className="border-zinc-800 bg-zinc-900/80">
                <CardHeader className="pb-2">
                  <CardDescription className="flex items-center gap-2 text-zinc-400">
                    <Users className="h-4 w-4" aria-hidden="true" />
                    Всего пользователей
                  </CardDescription>
                  <CardTitle className="text-4xl font-bold tabular-nums">{stats.total}</CardTitle>
                </CardHeader>
                <CardContent className="text-xs text-zinc-500">
                  Уникальных установок за всё время
                </CardContent>
              </Card>

              <Card className="border-zinc-800 bg-zinc-900/80">
                <CardHeader className="pb-2">
                  <CardDescription className="flex items-center gap-2 text-zinc-400">
                    <UserPlus className="h-4 w-4" aria-hidden="true" />
                    Новые сегодня
                  </CardDescription>
                  <CardTitle className="text-4xl font-bold tabular-nums">
                    {stats.newToday}
                  </CardTitle>
                </CardHeader>
                <CardContent className="text-xs text-zinc-500">
                  Первое подключение с начала суток
                </CardContent>
              </Card>

              <Card className="border-zinc-800 bg-zinc-900/80">
                <CardHeader className="pb-2">
                  <CardDescription className="flex items-center gap-2 text-zinc-400">
                    <Clock3 className="h-4 w-4" aria-hidden="true" />
                    Активны за 24 часа
                  </CardDescription>
                  <CardTitle className="text-4xl font-bold tabular-nums">
                    {stats.active24h}
                  </CardTitle>
                </CardHeader>
                <CardContent className="text-xs text-zinc-500">
                  Хотя бы один heartbeat за сутки
                </CardContent>
              </Card>
            </section>

            {/* Trend chart */}
            <Card className="border-zinc-800 bg-zinc-900/80">
              <CardHeader className="flex flex-row items-start justify-between gap-3">
                <div>
                  <CardTitle className="flex items-center gap-2 text-base">
                    <Activity className="h-4 w-4 text-emerald-400" aria-hidden="true" />
                    Пользователи в сети за 24 часа
                  </CardTitle>
                  <CardDescription className="mt-1">
                    Среднее число онлайн-клиентов по 15-минутным интервалам
                  </CardDescription>
                </div>
                <Badge
                  variant="outline"
                  className="mt-1 shrink-0 border-zinc-700 text-zinc-400"
                >
                  {trendData.length} точек
                </Badge>
              </CardHeader>
              <CardContent>
                {trendData.length === 0 ? (
                  <div className="flex h-64 flex-col items-center justify-center gap-2 text-center">
                    <Layers className="h-8 w-8 text-zinc-700" aria-hidden="true" />
                    <p className="text-sm text-zinc-500">
                      Данных для графика пока нет
                    </p>
                    <p className="text-xs text-zinc-600">
                      Снимки онлайна появятся после первых heartbeat от клиентов
                    </p>
                  </div>
                ) : (
                  <div className="h-64 w-full">
                    <ResponsiveContainer width="100%" height="100%">
                      <AreaChart
                        data={trendData}
                        margin={{ top: 8, right: 8, bottom: 0, left: -18 }}
                      >
                        <defs>
                          <linearGradient id="onlineFill" x1="0" y1="0" x2="0" y2="1">
                            <stop offset="0%" stopColor="#10b981" stopOpacity={0.45} />
                            <stop offset="100%" stopColor="#10b981" stopOpacity={0.02} />
                          </linearGradient>
                        </defs>
                        <CartesianGrid stroke="#27272a" strokeDasharray="3 3" vertical={false} />
                        <XAxis
                          dataKey="label"
                          tick={{ fill: "#71717a", fontSize: 11 }}
                          tickLine={false}
                          axisLine={{ stroke: "#3f3f46" }}
                          minTickGap={28}
                        />
                        <YAxis
                          tick={{ fill: "#71717a", fontSize: 11 }}
                          tickLine={false}
                          axisLine={false}
                          allowDecimals={false}
                        />
                        <Tooltip
                          contentStyle={{
                            background: "#18181b",
                            border: "1px solid #3f3f46",
                            borderRadius: 8,
                            color: "#e4e4e7",
                            fontSize: 12,
                          }}
                          labelStyle={{ color: "#a1a1aa" }}
                          formatter={(value) => [String(value), "В сети"]}
                        />
                        <Area
                          type="monotone"
                          dataKey="online"
                          stroke="#10b981"
                          strokeWidth={2}
                          fill="url(#onlineFill)"
                          isAnimationActive={false}
                        />
                      </AreaChart>
                    </ResponsiveContainer>
                  </div>
                )}
              </CardContent>
            </Card>

            {/* Breakdowns */}
            <section
              aria-label="Распределения"
              className="grid grid-cols-1 gap-4 lg:grid-cols-3"
            >
              <Card className="border-zinc-800 bg-zinc-900/80">
                <CardHeader className="pb-3">
                  <CardTitle className="text-sm font-medium text-zinc-300">
                    Версии приложения
                  </CardTitle>
                </CardHeader>
                <CardContent>
                  <BreakdownList items={stats.versions} emptyText="Данных пока нет" />
                </CardContent>
              </Card>
              <Card className="border-zinc-800 bg-zinc-900/80">
                <CardHeader className="pb-3">
                  <CardTitle className="flex items-center gap-2 text-sm font-medium text-zinc-300">
                    <MonitorSmartphone className="h-4 w-4" aria-hidden="true" />
                    Операционные системы
                  </CardTitle>
                </CardHeader>
                <CardContent>
                  <BreakdownList items={stats.oses} emptyText="Данных пока нет" />
                </CardContent>
              </Card>
              <Card className="border-zinc-800 bg-zinc-900/80">
                <CardHeader className="pb-3">
                  <CardTitle className="text-sm font-medium text-zinc-300">
                    Активные стратегии
                  </CardTitle>
                </CardHeader>
                <CardContent>
                  <BreakdownList items={stats.strategies} emptyText="Данных пока нет" />
                </CardContent>
              </Card>
            </section>

            {/* Clients */}
            <Card className="border-zinc-800 bg-zinc-900/80">
              <CardHeader className="gap-4">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div>
                    <CardTitle className="text-base">Последние клиенты</CardTitle>
                    <CardDescription className="mt-1">
                      Идентификаторы анонимизированы (первые символы хеша). Личные данные не
                      собираются и не хранятся.
                    </CardDescription>
                  </div>
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={exportCsv}
                    disabled={clients.length === 0}
                    className="border-zinc-800 bg-zinc-900 text-zinc-300 hover:bg-zinc-800 hover:text-zinc-100"
                  >
                    <Download className="mr-1.5 h-4 w-4" aria-hidden="true" />
                    CSV
                  </Button>
                </div>

                <div className="flex flex-wrap items-center gap-2">
                  <div className="relative w-full sm:w-64">
                    <Search
                      className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-zinc-500"
                      aria-hidden="true"
                    />
                    <Input
                      value={query}
                      onChange={(e) => setQuery(e.target.value)}
                      placeholder="Поиск по клиенту, версии, ОС…"
                      aria-label="Поиск по клиентам"
                      className="h-9 border-zinc-800 bg-zinc-950 pl-8 text-sm"
                    />
                  </div>
                  <div className="flex items-center gap-1 rounded-lg border border-zinc-800 bg-zinc-950 p-1">
                    {(
                      [
                        ["all", "Все"],
                        ["online", "В сети"],
                        ["offline", "Офлайн"],
                      ] as [OnlineFilter, string][]
                    ).map(([value, label]) => (
                      <button
                        key={value}
                        type="button"
                        onClick={() => setOnlineFilter(value)}
                        aria-pressed={onlineFilter === value}
                        className={`rounded-md px-2.5 py-1 text-xs font-medium transition-colors ${
                          onlineFilter === value
                            ? "bg-zinc-800 text-zinc-100"
                            : "text-zinc-500 hover:text-zinc-300"
                        }`}
                      >
                        {label}
                      </button>
                    ))}
                  </div>
                  <span className="ml-auto text-xs tabular-nums text-zinc-500">
                    {clients.length} из {stats.recent.length}
                  </span>
                </div>
              </CardHeader>
              <CardContent className="max-h-96 overflow-y-auto">
                {stats.recent.length === 0 ? (
                  <div className="flex flex-col items-center gap-2 py-10 text-center">
                    <Users className="h-8 w-8 text-zinc-700" aria-hidden="true" />
                    <p className="text-sm text-zinc-500">Пока нет ни одного клиента</p>
                    <p className="max-w-sm text-xs text-zinc-600">
                      Запустите WhyNoDPI с включённой телеметрией — данные появятся здесь в
                      течение минуты.
                    </p>
                  </div>
                ) : clients.length === 0 ? (
                  <p className="py-10 text-center text-sm text-zinc-500">
                    Ничего не найдено. Измените запрос или фильтр.
                  </p>
                ) : (
                  <Table>
                    <TableHeader>
                      <TableRow className="border-zinc-800 hover:bg-transparent">
                        <TableHead className="text-zinc-400">Клиент</TableHead>
                        <TableHead className="text-zinc-400">Версия</TableHead>
                        <TableHead className="text-zinc-400">ОС</TableHead>
                        <TableHead className="text-zinc-400">Стратегия</TableHead>
                        <TableHead className="text-zinc-400">Статус</TableHead>
                        <TableHead className="text-right text-zinc-400">Активность</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {clients.map((c) => (
                        <TableRow key={c.idMasked} className="border-zinc-800/70">
                          <TableCell className="font-mono text-xs text-zinc-300">
                            {c.idMasked}…
                          </TableCell>
                          <TableCell className="text-sm text-zinc-300">{c.appVersion}</TableCell>
                          <TableCell className="text-sm text-zinc-400">{c.osInfo}</TableCell>
                          <TableCell
                            className="max-w-40 truncate text-sm text-zinc-400"
                            title={c.strategy}
                          >
                            {c.strategy}
                          </TableCell>
                          <TableCell>
                            {c.online ? (
                              <Badge className="border border-emerald-500/40 bg-emerald-500/15 text-emerald-300 hover:bg-emerald-500/15">
                                <CheckCircle2 className="mr-1 h-3 w-3" aria-hidden="true" />в сети
                              </Badge>
                            ) : (
                              <Badge
                                variant="outline"
                                className="border-zinc-700 text-zinc-500 hover:bg-zinc-800 hover:text-zinc-300"
                              >
                                офлайн
                              </Badge>
                            )}
                          </TableCell>
                          <TableCell
                            className="text-right text-sm text-zinc-400"
                            title={fmtClock(c.lastSeenAt)}
                          >
                            {fmtRelative(c.lastSeenAt, nowMs)}
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                )}
              </CardContent>
            </Card>

            {/* Danger zone */}
            <Card className="border-red-900/50 bg-red-950/20">
              <CardHeader className="pb-3">
                <CardTitle className="text-sm font-medium text-red-300">Опасная зона</CardTitle>
                <CardDescription className="text-zinc-400">
                  Полная очистка телеметрии: удалит всех клиентов, историю онлайна и начнёт сбор
                  заново. Журнал аудита сохраняется.
                </CardDescription>
              </CardHeader>
              <CardContent className="space-y-3">
                <Button
                  variant="outline"
                  onClick={() => {
                    setResetMessage(null);
                    setConfirmOpen(true);
                  }}
                  className="border-red-800/60 bg-red-950/40 text-red-300 hover:bg-red-900/40 hover:text-red-200"
                >
                  <Trash2 className="mr-2 h-4 w-4" aria-hidden="true" />
                  Очистить все данные
                </Button>
                {resetMessage && (
                  <p
                    role="status"
                    className={`rounded-lg border p-3 text-sm ${
                      resetMessage.startsWith("Телеметрия")
                        ? "border-emerald-500/40 bg-emerald-500/10 text-emerald-300"
                        : "border-red-500/40 bg-red-500/10 text-red-300"
                    }`}
                  >
                    {resetMessage}
                  </p>
                )}
              </CardContent>
            </Card>
          </div>
        )}
      </main>

      <AlertDialog open={confirmOpen} onOpenChange={setConfirmOpen}>
        <AlertDialogContent className="border-zinc-800 bg-zinc-900 text-zinc-100">
          <AlertDialogHeader>
            <AlertDialogTitle>Удалить всю телеметрию?</AlertDialogTitle>
            <AlertDialogDescription className="text-zinc-400">
              Будут удалены все клиенты и история онлайна. Действие необратимо. Для подтверждения
              введите слово <span className="font-mono text-red-300">УДАЛИТЬ</span>.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <Input
            value={confirmText}
            onChange={(e) => setConfirmText(e.target.value)}
            placeholder="УДАЛИТЬ"
            className="border-zinc-800 bg-zinc-950"
            aria-label="Подтверждение удаления"
          />
          <AlertDialogFooter>
            <AlertDialogCancel className="border-zinc-800 bg-transparent text-zinc-300 hover:bg-zinc-800 hover:text-zinc-100">
              Отмена
            </AlertDialogCancel>
            <AlertDialogAction
              disabled={confirmText !== "УДАЛИТЬ" || resetting}
              onClick={(e) => {
                e.preventDefault();
                void resetData();
              }}
              className="bg-red-700 text-white hover:bg-red-600"
            >
              {resetting ? (
                <>
                  <Loader2 className="mr-2 h-4 w-4 animate-spin" aria-hidden="true" />
                  Удаление…
                </>
              ) : (
                "Да, удалить всё"
              )}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <footer className="mt-auto border-t border-zinc-800 py-4">
        <div className="mx-auto max-w-6xl px-4 text-center text-xs text-zinc-600 sm:px-6">
          WhyNoDPI telemetry · heartbeat каждые 60 с · данные хранятся анонимно · сессия
          администратора шифруется httpOnly-cookie и истекает автоматически
        </div>
      </footer>
    </div>
  );
}
