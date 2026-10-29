// Rendered per request, so the build really produces server code for the Worker.
export const dynamic = "force-dynamic";

export default function Page() {
  return <h1>Aedis example SSR site, rendered at {new Date().toISOString()}</h1>;
}
