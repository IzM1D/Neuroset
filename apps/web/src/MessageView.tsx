import { useEffect, useRef, useState, type ComponentPropsWithoutRef } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math-extended';
import rehypeHighlight from 'rehype-highlight';
import rehypeKatex from 'rehype-katex';
import 'katex/dist/katex.min.css';
import { Check, Copy, Pencil, RotateCcw } from 'lucide-react';
import type { Message } from './types';
import { ImageCard, ToolSteps } from './ToolResults';
import AttachmentList from './AttachmentList';
import type { CompletionActivity } from './api';

export interface RunProgress {
  status: string;
  startedAt: number;
  lastSignalAt?: number;
  activity?: CompletionActivity;
}

const activityLabels: Record<CompletionActivity, string> = {
  request: 'Ожидание первого сигнала от API',
  headers: 'API открыл ответ',
  data: 'Получены данные от API',
  reasoning: 'Получены фрагменты размышлений',
  answer: 'Получены фрагменты ответа',
  'tool-call': 'Получен вызов инструмента',
};

function GenerationProgress({ progress }: { progress: RunProgress }) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);
  const seconds = Math.max(0, Math.floor((now - (progress.lastSignalAt ?? progress.startedAt)) / 1000));
  const elapsed = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  return <div className="thinking">
    <span aria-hidden="true" /><span aria-hidden="true" /><span aria-hidden="true" />
    <small>
      <span role="status">{progress.status}{progress.activity && ` · ${activityLabels[progress.activity]}`}</span>
      <span className="thinking-elapsed"> · {progress.lastSignalAt !== undefined ? 'С последнего сигнала' : 'Прошло'} {elapsed}</span>
    </small>
  </div>;
}

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

export default function MessageView({ message, busy, progress, canRetry, onEdit, onRetry, onCopyError }: {
  message: Message;
  busy: boolean;
  progress: RunProgress | null;
  canRetry: boolean;
  onEdit: () => void;
  onRetry: () => void;
  onCopyError: () => void;
}) {
  return <article className={`message message-${message.role}`} aria-label={message.role === 'user' ? 'Ваше сообщение' : 'Ответ модели'}>
    {message.role === 'assistant' && <div className="assistant-label"><span className="mini-mark">N</span>Neuroset</div>}
    <div className="message-content">
      {!!message.steps?.length && <ToolSteps steps={message.steps} />}
      {!!message.attachments?.length && <AttachmentList attachments={message.attachments} />}
      {message.reasoning && <details className="reasoning">
        <summary>Размышления от API</summary>
        <div>{message.reasoning}</div>
      </details>}
      {message.role === 'user' ? (message.content && <div className="user-bubble">{message.content}</div>) :
        <div className="markdown"><ReactMarkdown remarkPlugins={[remarkGfm, [remarkMath, { singleDollarTextMath: false }]]} rehypePlugins={[rehypeKatex, rehypeHighlight]} components={{
          pre: CodeBlock,
          a: ({ children, ...props }) => <a {...props} target="_blank" rel="noopener noreferrer">{children}</a>,
          // External images aren't fetched automatically from model-authored Markdown.
          img: ({ alt, src }) => <a href={src} target="_blank" rel="noopener noreferrer">{alt || 'Открыть изображение'}</a>,
        }}>{message.content}</ReactMarkdown></div>}
      {message.images?.map(image => <ImageCard key={image.id} image={image} />)}
      {progress && <GenerationProgress progress={progress} />}
      {message.error && <p className="error-notice" role="alert">{message.error}</p>}
      {message.interrupted && !progress && <p className="interrupted">Генерация остановлена</p>}
    </div>
    {!progress && <div className="message-actions">
      {message.content && <CopyButton text={message.content} onError={onCopyError} />}
      {message.role === 'user' && <button className="icon-button" title="Редактировать сообщение" aria-label="Редактировать сообщение" disabled={busy} onClick={onEdit}><Pencil size={15} /></button>}
      {canRetry && <button className="icon-button" title="Повторить ответ" aria-label="Повторить ответ" disabled={busy} onClick={onRetry}><RotateCcw size={16} /></button>}
    </div>}
  </article>;
}
