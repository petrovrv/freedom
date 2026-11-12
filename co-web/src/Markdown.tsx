// SPDX-License-Identifier: AGPL-3.0-or-later
import DOMPurify from 'dompurify';
import 'katex/dist/katex.min.css';
import { marked } from 'marked';
import { useMemo } from 'react';
import { cutMath, pasteMath } from './mathText';

marked.setOptions({ gfm: true, breaks: false });

DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName === 'A') {
    node.setAttribute('target', '_blank');
    node.setAttribute('rel', 'noopener noreferrer');
  }
});

export function Markdown({ text }: { text: string }) {
  const html = useMemo(() => {
    // 0.78.0: formulas (mathText.ts) - cut out before marked, KaTeX HTML put back after the sanitizer
    const m = cutMath(text || '');
    return pasteMath(DOMPurify.sanitize(marked.parse(m.text, { async: false }) as string), m);
  }, [text]);
  return <div className="md" dangerouslySetInnerHTML={{ __html: html }} />;
}
