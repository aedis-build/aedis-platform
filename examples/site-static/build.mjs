// The "build" of the example: copies public/ to out/, like a static export would.
import { cpSync, rmSync } from "node:fs";

rmSync("out", { recursive: true, force: true });
cpSync("public", "out", { recursive: true });
