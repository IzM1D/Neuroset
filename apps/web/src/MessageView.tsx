import { useRef, useState, type ComponentPropsWithoutRef } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeHighlight from 'rehype-highlight';
import { Check, Copy, Pencil, RotateCcw } from 'lucide-react';
import type { Message } from './types';

function CopyButton({ text, label = 'Копировать', onError }: { text: string | (() => string); label?: string; onError?: () => void }) {
  const [copied, setCopied] = useState(false);
  return <button className="icon-button" aria-label={copied ? 'Скопировано' : label} title={copied ? 'Скопировано' : label} onClick={async () => {
    try {
      await navigator.clipboard.writeText(typeof text === 'function' ? text() : text);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1800);
    } catch { onError?.(); }
  }}>{copied ? <Check size={16} /> : <Copy size={16} />}</button>;
}

function CodeBlock({ children, ...props }: ComponentPropsWithoutRef<'pre'>) {
  const ref = useRef<HTMLPreElement>(null);
  const [failed, setFailed] = useState(false);
  return <div className="code-block">
    <div className="code-toolbar"><span>{failed ? 'Выделите код и скопируйте вручную' : 'Код'}</span><CopyButton text={() => ref.current?.textContent || ''} label="Копировать код" onError={() => setFailed(true)} /></div>
    <pre ref={ref} {...props}>{children}</pre>
  </div>;
}

export default function MessageView({ message, busy, generating, canRetry, onEdit, onRetry, onCopyError }: {
  message: Message;
  busy: boolean;
  generating: boolean;
  canRetry: boolean;
  onEdit: () => void;
  onRetry: () => void;
  onCopyError: () => void;
}) {
  return <article className={`message message-${message.role}`} aria-label={message.role === 'user' ? 'Ваше сообщение' : 'Ответ модели'}>
    {message.role === 'assistant' && <div className="assistant-label"><span className="mini-mark">N</span>Neuroset</div>}
    <div className="message-content">
      {message.role === 'user' ? <div className="user-bubble">{message.content}</div> :
        <div className="markdown"><ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeHighlight]} components={{
          pre: CodeBlock,
          a: ({ children, ...props }) => <a {...props} target="_blank" rel="noopener noreferrer">{children}</a>,
          // External images aren't fetched automatically from model-authored Markdown.
          img: ({ alt, src }) => <a href={src} target="_blank" rel="noopener noreferrer">{alt || 'Открыть изображение'}</a>,
        }}>{message.content}</ReactMarkdown></div>}
      {generating && !message.content && <div className="thinking" role="status"><span /><span /><span /><span className="sr-only">Модель готовит ответ</span></div>}
      {message.error && <p className="error-notice" role="alert">{message.error}</p>}
      {message.interrupted && !generating && <p className="interrupted">Генерация остановлена</p>}
    </div>
    {!generating && <div className="message-actions">
      {message.content && <CopyButton text={message.content} onError={onCopyError} />}
      {message.role === 'user' && <button className="icon-button" title="Редактировать сообщение" aria-label="Редактировать сообщение" disabled={busy} onClick={onEdit}><Pencil size={15} /></button>}
      {canRetry && <button className="icon-button" title="Повторить ответ" aria-label="Повторить ответ" disabled={busy} onClick={onRetry}><RotateCcw size={16} /></button>}
    </div>}
  </article>;
}
