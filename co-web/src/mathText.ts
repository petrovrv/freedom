// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.78.0: formulas in Markdown ($...$, $$...$$, \(...\), \[...\]) rendered with KaTeX. Math is cut out before
// marked (so Markdown does not eat _ and *), replaced by placeholders, and put back as KaTeX HTML after DOMPurify:
// KaTeX escapes its input itself (trust: false), and the sanitizer would otherwise strip its MathML.
import katex from 'katex';

export interface MathCut {
  text: string;
  html: string[];
  src: string[];
  tag: string;
}

// Placeholders carry a random letters-only tag per message, so text that merely looks like one is never replaced.
const newTag = () => Array.from({ length: 10 }, () => String.fromCharCode(97 + Math.floor(Math.random() * 26))).join('');

// Code (fenced blocks and inline spans) is never touched.
const CODE_RE = /(```[\s\S]*?(?:```|$)|~~~[\s\S]*?(?:~~~|$)|``[^\n]*?``|`[^`\n]*`)/;

// Display first, then inline. Inline $...$: no space after the opening or before the closing $, no newline, and the
// closing $ not followed by a digit (so "$5 and $10" stays text).
const MATH_RE = /\$\$([\s\S]+?)\$\$|\\\[([\s\S]+?)\\\]|\\\(([\s\S]+?)\\\)|(?<![\\$\w])\$(?!\s)([^$\n]+?)(?<!\s)\$(?![\d$])/g;

function render(tex: string, display: boolean): string {
  try {
    return katex.renderToString(tex.trim(), { displayMode: display, throwOnError: false, trust: false, strict: 'ignore', output: 'htmlAndMathml' });
  } catch {
    return '';
  }
}

export function cutMath(src: string): MathCut {
  const html: string[] = [];
  const srcs: string[] = [];
  const tag = newTag();
  if (!/[$\\]/.test(src)) return { text: src, html, src: srcs, tag };
  const parts = src.split(CODE_RE);
  for (let k = 0; k < parts.length; k += 2) {
    // even = prose, odd = code (split keeps the captured code)
    parts[k] = parts[k].replace(MATH_RE, (whole, d1, d2, i1, i2) => {
      const display = d1 !== undefined || d2 !== undefined;
      const out = render((d1 ?? d2 ?? i1 ?? i2) as string, display);
      if (!out) return whole;
      html.push(display ? `<span class="math-display">${out}</span>` : out);
      srcs.push(whole);
      return `COMATH${tag}P${html.length - 1}X`;
    });
  }
  return { text: parts.join(''), html, src: srcs, tag };
}

const escAttr = (s: string) => s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

// 0.78.1: KaTeX HTML goes only into text; a placeholder inside a tag (link URL, title) gets its source back, so
// [a](https://x/$y$) stays a working link. DOMPurify writes every attribute in double quotes with " escaped.
const TAG_RE = /(<(?:[^>"]|"[^"]*")*>)/;

export function pasteMath(sanitized: string, cut: MathCut): string {
  if (!cut.html.length) return sanitized;
  const ph = new RegExp(`COMATH${cut.tag}P(\\d+)X`, 'g');
  return sanitized
    .split(TAG_RE)
    .map((part, k) =>
      k % 2
        ? part.replace(ph, (whole, n: string) => (cut.src[Number(n)] !== undefined ? escAttr(cut.src[Number(n)]) : whole))
        : part.replace(ph, (whole, n: string) => cut.html[Number(n)] ?? whole),
    )
    .join('');
}
