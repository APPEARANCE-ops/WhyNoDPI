import { readSessionFromCookies } from "@/lib/admin-session";
import { isPasswordConfigured } from "@/lib/admin-auth";
import AdminLogin from "./admin-login";
import AdminDashboard from "./admin-dashboard";

export const dynamic = "force-dynamic";

export default async function Home() {
  const session = await readSessionFromCookies();

  if (!session) {
    return <AdminLogin configured={isPasswordConfigured()} />;
  }

  return <AdminDashboard />;
}
