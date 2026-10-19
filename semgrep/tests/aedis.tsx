// Fixtures for semgrep/rules/aedis.yml (semgrep --test). Not compiled.
import ReactMarkdown from "react-markdown";
// ruleid: aedis.rehype-raw
import rehypeRaw from "rehype-raw";

declare function Textarea(): JSX.Element;

export function fixtures(html: string, token: string) {
  // ruleid: aedis.secret-in-web-storage
  localStorage.setItem("access_token", token);
  // ruleid: aedis.secret-in-web-storage
  sessionStorage.setItem("SessionId", token);
  // ok: aedis.secret-in-web-storage
  localStorage.setItem("theme", "dark");
  // ruleid: aedis.fetch-credentials-include
  void fetch("https://api.example.com", { credentials: "include" });
  // ok: aedis.fetch-credentials-include
  void fetch("/api/me", { credentials: "same-origin" });
  // ruleid: aedis.dangerously-set-inner-html
  const markup = <div dangerouslySetInnerHTML={{ __html: html }} />;
  // ruleid: aedis.raw-text-field
  const notes = <textarea />;
  // ruleid: aedis.raw-text-field
  const name = <input type="text" />;
  // ok: aedis.raw-text-field
  const bounded = <Textarea />;
  const markdown = <ReactMarkdown rehypePlugins={[rehypeRaw]}>{html}</ReactMarkdown>;
  return [markup, notes, name, bounded, markdown];
}
