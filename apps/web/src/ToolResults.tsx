import { useEffect, useState } from 'react';
import { Check, ChevronDown, Download, ExternalLink, LoaderCircle, X } from 'lucide-react';
import { loadImage } from './assets';
import type { ImageArtifact, ToolStep } from './types';

function pretty(value: string) { try { return JSON.stringify(JSON.parse(value), null, 2); } catch { return value; } }

export function ToolSteps({ steps }: { steps: ToolStep[] }) {
  return <div className="tool-steps">{steps.map(step => <details className={`tool-step ${step.status}`} key={step.id}>
    <summary>{step.status === 'running' ? <LoaderCircle size={15} className="spin" /> : step.status === 'done' ? <Check size={15} /> : <X size={15} />}<span>{step.label}</span><span className="tool-step-state">{step.status === 'running' ? 'Выполняется' : step.status === 'done' ? 'Готово' : 'Ошибка'}</span><ChevronDown size={13} /></summary>
    <div className="tool-step-body"><h4>{step.name === 'run_python' ? 'Код' : 'Запрос'}</h4><pre>{step.name === 'run_python' ? (() => { try { return JSON.parse(step.input).code; } catch { return step.input; } })() : pretty(step.input)}</pre>
      {step.output && <><h4>Результат</h4><pre>{pretty(step.output)}</pre></>}
      {step.sources?.length ? <div className="sources">{step.sources.map(source => <a key={source.url} href={source.url} target="_blank" rel="noopener noreferrer"><ExternalLink size={13} />{source.title}</a>)}</div> : null}
    </div>
  </details>)}</div>;
}

export function ImageCard({ image }: { image: ImageArtifact }) {
  const [url, setUrl] = useState('');
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    let objectUrl = '';
    loadImage(image.id).then(blob => {
      if (!active) return;
      if (!blob) { setError('Изображение отсутствует в хранилище браузера.'); return; }
      objectUrl = URL.createObjectURL(blob);
      setUrl(objectUrl);
    }).catch(() => { if (active) setError('Не удалось загрузить изображение.'); });
    return () => { active = false; URL.revokeObjectURL(objectUrl); };
  }, [image.id]);
  return <figure className="generated-image">
    {url ? <a href={url} target="_blank" rel="noopener noreferrer" title="Открыть изображение"><img src={url} alt={image.title} /></a> : <p>{error || 'Загрузка изображения…'}</p>}
    <figcaption><span><strong>{image.title}</strong><small>{image.model}</small></span>{url && <a href={url} download={`${image.kind}-${image.id.slice(0, 8)}.png`} title="Скачать изображение" aria-label="Скачать изображение" className="icon-button"><Download size={17} /></a>}</figcaption>
  </figure>;
}
