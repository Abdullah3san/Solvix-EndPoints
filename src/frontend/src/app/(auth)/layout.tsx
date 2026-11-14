import { SolvixWordmark } from "@/components/shell/logo";
import { ThemeToggle } from "@/components/shell/theme-toggle";

export default function AuthLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="grid min-h-dvh lg:grid-cols-[1.1fr_1fr]">
      <section className="relative hidden flex-col justify-between bg-sidebar p-10 text-sidebar-foreground lg:flex">
        <SolvixWordmark className="text-sidebar-foreground" />
        <div className="max-w-md space-y-4">
          <h2 className="text-3xl font-semibold leading-tight">One platform for every site, team and endpoint.</h2>
          <p className="text-sm text-sidebar-muted">
            Solvix keeps each organisation strictly isolated: your branches, departments and members are never visible to
            another workspace, enforced in the application and in the database.
          </p>
        </div>
        <p className="text-xs text-sidebar-muted">© {new Date().getFullYear()} Solvix</p>
      </section>
      <section className="flex flex-col">
        <div className="flex items-center justify-between p-4 lg:justify-end">
          <SolvixWordmark className="lg:hidden" />
          <ThemeToggle />
        </div>
        <div className="flex flex-1 items-center justify-center p-6">
          <div className="w-full max-w-md">{children}</div>
        </div>
      </section>
    </div>
  );
}
