import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import RootLayout from "@/app/layout";
import Home from "@/app/page";

describe("página inicial", () => {
  it("renderiza o nome do site dentro do layout", () => {
    render(<RootLayout><Home /></RootLayout>);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("__AEDIS_NAME__");
    expect(screen.getByRole("main")).toBeInTheDocument();
  });
});
