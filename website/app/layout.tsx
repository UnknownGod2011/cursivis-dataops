import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  metadataBase: new URL("https://cursiviss.vercel.app"),
  title: "Cursivis — OpenAI, right where your cursor is",
  description:
    "A cursor-first Windows productivity system for selected text, images, guided actions, prompt optimization, and OpenAI Realtime conversations.",
  openGraph: {
    title: "Cursivis — OpenAI, right where your cursor is",
    description: "Select context. Choose less. Keep moving.",
    url: "https://cursiviss.vercel.app",
    siteName: "Cursivis",
    type: "website",
    images: [{ url: "/og.png", width: 1732, height: 909, alt: "Cursivis — OpenAI, right where your cursor is" }],
  },
  twitter: {
    card: "summary_large_image",
    title: "Cursivis — OpenAI, right where your cursor is",
    description: "Select context. Choose less. Keep moving.",
    images: ["/og.png"],
  },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
